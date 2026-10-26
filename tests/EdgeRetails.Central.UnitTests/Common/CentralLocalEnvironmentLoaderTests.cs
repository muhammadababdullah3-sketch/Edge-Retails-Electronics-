using EdgeRetails.Central.Application.Common;

namespace EdgeRetails.Central.UnitTests.Common;

public sealed class CentralLocalEnvironmentLoaderTests : IDisposable
{
    private readonly string _previousValue;
    private readonly string? _previousFile;

    public CentralLocalEnvironmentLoaderTests()
    {
        _previousValue = Environment.GetEnvironmentVariable("CENTRAL_LOADER_TEST_KEY") ?? string.Empty;
        _previousFile = Environment.GetEnvironmentVariable(CentralLocalEnvironmentLoader.LocalEnvFileVariable);
        Environment.SetEnvironmentVariable("CENTRAL_LOADER_TEST_KEY", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CENTRAL_LOADER_TEST_KEY", string.IsNullOrEmpty(_previousValue) ? null : _previousValue);
        Environment.SetEnvironmentVariable(CentralLocalEnvironmentLoader.LocalEnvFileVariable, _previousFile);
    }

    [Fact]
    public void ApplyDotEnvFile_SetsUnsetVariablesOnly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"central-env-{Guid.NewGuid():N}.env");
        File.WriteAllText(path, """
            # comment
            CENTRAL_LOADER_TEST_KEY=from-file
            ALREADY_SET=ignored
            """);

        Environment.SetEnvironmentVariable("ALREADY_SET", "process-value");

        CentralLocalEnvironmentLoader.ApplyDotEnvFile(path);

        Assert.Equal("from-file", Environment.GetEnvironmentVariable("CENTRAL_LOADER_TEST_KEY"));
        Assert.Equal("process-value", Environment.GetEnvironmentVariable("ALREADY_SET"));

        File.Delete(path);
        Environment.SetEnvironmentVariable("ALREADY_SET", null);
    }

    [Fact]
    public void TryApplyFromRepository_FindsLocalFileUnderAncestor()
    {
        var root = Path.Combine(Path.GetTempPath(), $"central-repo-{Guid.NewGuid():N}");
        var localDir = Path.Combine(root, "local");
        Directory.CreateDirectory(localDir);
        var envPath = Path.Combine(localDir, "central-dev.env");
        File.WriteAllText(envPath, "CENTRAL_LOADER_TEST_KEY=ancestor\n");

        var nested = Path.Combine(root, "src", "EdgeRetails.Central.Api", "bin");
        Directory.CreateDirectory(nested);

        var applied = CentralLocalEnvironmentLoader.TryApplyFromRepository(nested, "Development");

        Assert.True(applied);
        Assert.Equal("ancestor", Environment.GetEnvironmentVariable("CENTRAL_LOADER_TEST_KEY"));

        Directory.Delete(root, recursive: true);
    }
}
