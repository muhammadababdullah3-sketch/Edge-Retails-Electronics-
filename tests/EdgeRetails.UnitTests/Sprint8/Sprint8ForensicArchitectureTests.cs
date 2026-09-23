using Xunit;

namespace EdgeRetails.UnitTests.Sprint8;

public sealed class Sprint8ForensicArchitectureTests
{
    [Fact]
    public void Sprint8Patch_DoesNotCreateScreen18()
    {
        var root = FindRepoRoot();
        var desktop = Path.Combine(root, "src", "EdgeRetails.Desktop");
        Assert.True(Directory.Exists(desktop), $"Desktop source directory was not found: {desktop}");
        var offending = Directory.EnumerateFiles(desktop, "*.xaml", SearchOption.AllDirectories)
            .Where(path => Path.GetFileNameWithoutExtension(path).Contains("Screen18", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(offending);
    }

    [Fact]
    public void Desktop_DoesNotContainDirectDbContextOrNpgsqlPersistence()
    {
        var root = FindRepoRoot();
        var desktop = Path.Combine(root, "src", "EdgeRetails.Desktop");
        Assert.True(Directory.Exists(desktop), $"Desktop source directory was not found: {desktop}");
        var offenders = Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains("DbContext", StringComparison.Ordinal) ||
                       text.Contains("NpgsqlConnection", StringComparison.Ordinal) ||
                       text.Contains("SaveChanges(", StringComparison.Ordinal);
            }).ToArray();
        Assert.Empty(offenders);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EdgeRetails.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }
}
