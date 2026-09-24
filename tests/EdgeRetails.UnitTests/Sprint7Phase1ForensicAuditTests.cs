namespace EdgeRetails.UnitTests;

public sealed class Sprint7Phase1ForensicAuditTests
{
    private static string SolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "EdgeRetails.sln")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("EdgeRetails.sln was not found.");
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([SolutionRoot(), .. parts]));

    [Fact]
    public void Phase1_Defines_Installation_And_Identity_Authorities()
    {
        var installation = Read(
            "src", "EdgeRetails.Domain", "SystemConfiguration", "InstallationModels.cs");
        var identity = Read(
            "src", "EdgeRetails.Domain", "Identity", "IdentityModels.cs"); Assert.Contains("public sealed class InstallationState", installation);
        Assert.Contains("SetupStatus.Complete", Read(
            "src", "EdgeRetails.Application", "Features", "Setup",
            "FirstSetupBootstrapHandler.cs"));

        foreach (var contract in new[]
        {
            "public sealed class User",
            "public sealed class Role",
            "public sealed class Permission",
            "public sealed class RolePermission",
            "public sealed class UserPermissionOverride",
            "public sealed class UserSession"
        })
        {
            Assert.Contains(contract, identity);
        }

        Assert.Contains("PinHash", identity);
        Assert.Contains("PinSalt", identity);
        Assert.DoesNotContain("PlainPin", identity);
    }

    [Fact]
    public void Phase1_Concrete_Pin_Service_Uses_Strong_Derivation()
    {
        var source = Read(
            "src", "EdgeRetails.Infrastructure", "Services",
            "IdentitySetupServices.cs");

        Assert.Contains("Rfc2898DeriveBytes.Pbkdf2", source);
        Assert.Contains("HashAlgorithmName.SHA256", source);
        Assert.Contains("RandomNumberGenerator.GetBytes", source);
        Assert.Contains("CryptographicOperations.FixedTimeEquals", source);
        Assert.Contains("210_000", source);
    }
    [Fact]
    public void Phase1_Migration_Creates_Mandatory_Setup_Identity_Schema()
    {
        var migrationRoot = Path.Combine(
            SolutionRoot(),
            "src",
            "EdgeRetails.Infrastructure",
            "Persistence",
            "Migrations");

        var migration = Directory
            .EnumerateFiles(
                migrationRoot,
                "*_Sprint7Phase1SetupIdentity.cs",
                SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText)
            .Single();

        Assert.Contains("name: \"identity\"", migration);
        Assert.Contains("name: \"installation_state\"", migration);
        Assert.Contains("name: \"users\"", migration);
        Assert.Contains("name: \"roles\"", migration);
        Assert.Contains("name: \"permissions\"", migration);
        Assert.Contains("name: \"role_permissions\"", migration);
        Assert.Contains("name: \"user_permission_overrides\"", migration);
        Assert.Contains("name: \"user_sessions\"", migration);
        Assert.Contains("singleton_key = 'PRIMARY'", migration);
        Assert.Contains("pin_iterations > 0", migration);
    }

    [Fact]
    public void Phase1_Infrastructure_Registers_Setup_Identity_And_Readiness()
    {
        var source = Read(
            "src", "EdgeRetails.Infrastructure",
            "InfrastructureServiceCollectionExtensions.cs"); Assert.Contains("ISetupRepository, SetupRepository", source);
        Assert.Contains("IIdentityReadRepository, IdentityReadRepository", source);
        Assert.Contains("IPinCredentialService, Pbkdf2PinCredentialService", source);
        Assert.Contains(
            "IDatabaseReadinessService, EfDatabaseReadinessService",
            source);
    }

    [Fact]
    public void Phase1_Desktop_Does_Not_Directly_Own_Postgres()
    {
        var desktopRoot = Path.Combine(
            SolutionRoot(),
            "src",
            "EdgeRetails.Desktop");

        // BackendRuntime.cs is the architecturally sanctioned connection string resolver —
        // it delegates to Infrastructure via AddEdgeRetailsInfrastructure and is not a direct Postgres coupling leak.
        var combined = string.Join(
            "\n",
            Directory.EnumerateFiles(
                    desktopRoot,
                    "*.*",
                    SearchOption.AllDirectories)
                .Where(path =>
                    (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) &&
                    !path.EndsWith("BackendRuntime.cs", StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));

        Assert.DoesNotContain("NpgsqlConnection", combined);
        Assert.DoesNotContain("DbContext", combined);
        Assert.DoesNotContain("ConnectionString", combined);
    }
}
