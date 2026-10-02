namespace EdgeRetails.UnitTests;

public sealed class WorkerWindowsServiceLifetimeTests
{
    [Fact]
    public void WorkerHost_ConfiguresWindowsServiceLifetimeBeforeBuild()
    {
        var root = FindSolutionRoot();
        var project = File.ReadAllText(Path.Combine(
            root,
            "src",
            "EdgeRetails.Worker",
            "EdgeRetails.Worker.csproj"));
        var program = File.ReadAllText(Path.Combine(
            root,
            "src",
            "EdgeRetails.Worker",
            "Program.cs"));

        Assert.Contains(
            "Microsoft.Extensions.Hosting.WindowsServices",
            project,
            StringComparison.Ordinal);

        var serviceLifetime = program.IndexOf(
            "builder.Services.AddWindowsService(options => options.ServiceName = \"EdgeRetailsWorker\");",
            StringComparison.Ordinal);
        var buildHost = program.IndexOf("var host = builder.Build();", StringComparison.Ordinal);

        Assert.True(serviceLifetime >= 0, "Worker must register its Windows Service lifetime and SCM name.");
        Assert.True(buildHost > serviceLifetime, "Windows Service lifetime must be configured before the host is built.");
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EdgeRetails.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("EdgeRetails.sln was not found above the test output directory.");
    }
}
