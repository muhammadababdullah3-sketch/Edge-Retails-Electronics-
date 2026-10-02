using System.Net;
using EdgeRetails.Recovery;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class RecoveryServerRuntimePolicyTests
{
    [Theory]
    [InlineData("1.0.10", "1.0.10", true)]
    [InlineData("1.0.10", "1.0.10.0", true)]
    [InlineData("1.0.10", "1.0.10+1fb5d3b1f66b1cf8db23e0fe10eee030477545c7", true)]
    [InlineData("1.0.9", "1.0.10+1fb5d3b1f66b1cf8db23e0fe10eee030477545c7", false)]
    [InlineData("1.0.10", "1.0.9+1fb5d3b1f66b1cf8db23e0fe10eee030477545c7", false)]
    [InlineData("1.0.10", "1.0.100+1fb5d3b1f66b1cf8db23e0fe10eee030477545c7", false)]
    [InlineData("1.0.10", "1.0.10+not-a-commit", false)]
    [InlineData("1.0.10", "1.0.10-evil", false)]
    [InlineData("1.0.10", "1.0.10+1fb5d3b1f66b1cf8db23e0fe10eee030477545c7-extra", false)]
    [InlineData("1.0.10", "1.0.10+1fb5d3b1f66b1cf8db23e0fe10eee030477545c7\n", false)]
    public void InstalledVersion_RequiresExactFileReleaseAndApprovedProductMetadata(
        string fileVersion, string productVersion, bool expected)
    {
        Assert.Equal(expected, RecoveryServerRuntimePolicy.IsApprovedServerVersion(
            fileVersion, productVersion, "1.0.10"));
    }

    [Fact]
    public void ApprovedImagePath_MustBeTheExactProgramFilesServerBinary()
    {
        var programFiles = Path.Combine(Path.GetTempPath(), "Program Files");
        var installedServer = Path.Combine(programFiles, "Edge Retails", "server", "EdgeRetails.Server.exe");

        Assert.True(RecoveryServerRuntimePolicy.IsApprovedServerImagePath(installedServer, programFiles));
        Assert.False(RecoveryServerRuntimePolicy.IsApprovedServerImagePath(
            Path.Combine(programFiles, "Edge Retails", "server", "EdgeRetails.Server-copy.exe"), programFiles));
        Assert.False(RecoveryServerRuntimePolicy.IsApprovedServerImagePath(
            Path.Combine(Path.GetTempPath(), "EdgeRetails.Server.exe"), programFiles));
        Assert.False(RecoveryServerRuntimePolicy.IsApprovedServerImagePath("EdgeRetails.Server.exe", programFiles));
        Assert.False(RecoveryServerRuntimePolicy.IsApprovedServerImagePath(installedServer, null));
    }

    [Fact]
    public void ListenerPolicy_RequiresAtLeastOneListenerAndEveryListenerToBeLoopback()
    {
        Assert.True(RecoveryServerRuntimePolicy.HasOnlyLoopbackListeners(new[]
        {
            new IPEndPoint(IPAddress.Loopback, RecoveryServerRuntimePolicy.ShopServerPort),
            new IPEndPoint(IPAddress.IPv6Loopback, RecoveryServerRuntimePolicy.ShopServerPort)
        }));

        Assert.False(RecoveryServerRuntimePolicy.HasOnlyLoopbackListeners(Array.Empty<IPEndPoint>()));
        Assert.False(RecoveryServerRuntimePolicy.HasOnlyLoopbackListeners(new[]
        {
            new IPEndPoint(IPAddress.Loopback, RecoveryServerRuntimePolicy.ShopServerPort),
            new IPEndPoint(IPAddress.Parse("192.168.1.20"), RecoveryServerRuntimePolicy.ShopServerPort)
        }));
        Assert.False(RecoveryServerRuntimePolicy.HasOnlyLoopbackListeners(new[]
        {
            new IPEndPoint(IPAddress.Any, RecoveryServerRuntimePolicy.ShopServerPort)
        }));
        Assert.False(RecoveryServerRuntimePolicy.HasOnlyLoopbackListeners(new[]
        {
            new IPEndPoint(IPAddress.IPv6Any, RecoveryServerRuntimePolicy.ShopServerPort)
        }));
        Assert.False(RecoveryServerRuntimePolicy.HasOnlyLoopbackListeners(new[]
        {
            new IPEndPoint(IPAddress.Loopback, RecoveryServerRuntimePolicy.ShopServerPort + 1)
        }));
    }

    [Fact]
    public void InstalledImagePath_RejectsMissingComponentsAndAcceptsAnExistingNormalTree()
    {
        var trustedRoot = Path.Combine(Path.GetTempPath(), "EdgeRetails-Recovery-" + Guid.NewGuid().ToString("N"));
        var serverDirectory = Path.Combine(trustedRoot, "Edge Retails", "server");
        var serverPath = Path.Combine(serverDirectory, "EdgeRetails.Server.exe");

        try
        {
            Directory.CreateDirectory(serverDirectory);
            File.WriteAllBytes(serverPath, Array.Empty<byte>());

            Assert.True(RecoveryServerRuntimePolicy.HasNoReparsePointComponents(serverPath, trustedRoot));
            Assert.False(RecoveryServerRuntimePolicy.HasNoReparsePointComponents(
                Path.Combine(serverDirectory, "missing.exe"), trustedRoot));
            Assert.False(RecoveryServerRuntimePolicy.HasNoReparsePointComponents(
                Path.Combine(Path.GetTempPath(), "outside.exe"), trustedRoot));
        }
        finally
        {
            if (Directory.Exists(trustedRoot))
            {
                Directory.Delete(trustedRoot, recursive: true);
            }
        }
    }
}
