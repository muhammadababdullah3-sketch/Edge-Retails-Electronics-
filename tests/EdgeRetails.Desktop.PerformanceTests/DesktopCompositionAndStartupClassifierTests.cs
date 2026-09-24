using System.IO;
using System.Net.Sockets;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class DesktopCompositionAndStartupClassifierTests
{
    [Fact]
    public void StartupFailureClassifier_MissingDatabaseConfiguration_ClassifiesAsConfigurationRequired()
    {
        var ex = new InvalidOperationException("EDGE_RETAILS_DB is not configured and no persistent configuration file was found.");
        var result = StartupFailureClassifier.Classify(ex);

        Assert.Equal(StartupFailureCode.CONFIGURATION_REQUIRED, result.Code);
        Assert.Contains("Configuration", result.Title);
        Assert.Contains("EDGE_RETAILS_DB", result.RemediationGuidance);
    }

    [Fact]
    public void StartupFailureClassifier_SocketExceptionOrConnectionRefused_ClassifiesAsDatabaseUnavailable()
    {
        var socketEx = new SocketException(10061); // WSAECONNREFUSED
        var result = StartupFailureClassifier.Classify(socketEx);
        Assert.Equal(StartupFailureCode.DATABASE_UNAVAILABLE, result.Code);

        var wrappedEx = new InvalidOperationException("Failed to connect to server: Connection refused", socketEx);
        var wrappedResult = StartupFailureClassifier.Classify(wrappedEx);
        Assert.Equal(StartupFailureCode.DATABASE_UNAVAILABLE, wrappedResult.Code);
        Assert.Contains("5432", wrappedResult.RemediationGuidance);
    }

    [Fact]
    public void StartupFailureClassifier_PendingMigrations_ClassifiesAsDatabaseIncompatible()
    {
        var ex = new InvalidOperationException("EF Core model has pending model changes or pending migrations.");
        var result = StartupFailureClassifier.Classify(ex);

        Assert.Equal(StartupFailureCode.DATABASE_INCOMPATIBLE, result.Code);
        Assert.Contains("migration", result.RemediationGuidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartupFailureClassifier_DependencyInjectionFailure_ClassifiesAsDependencyGraphInvalid()
    {
        var ex = new InvalidOperationException("Unable to resolve service for type 'EdgeRetails.Application.Gateways.IApplicationGateway'.");
        var result = StartupFailureClassifier.Classify(ex);

        Assert.Equal(StartupFailureCode.DEPENDENCY_GRAPH_INVALID, result.Code);
        Assert.Contains("dependency injection", result.RemediationGuidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartupFailureClassifier_LicensePublicKeyIssue_ClassifiesAsLicenseConfigurationInvalid()
    {
        var ex = new InvalidOperationException("EDGE_RETAILS_LICENSE_PUBLIC_KEY is not configured or license.pub is missing.");
        var result = StartupFailureClassifier.Classify(ex);

        Assert.Equal(StartupFailureCode.LICENSE_CONFIGURATION_INVALID, result.Code);
        Assert.Contains("public key", result.RemediationGuidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartupFailureClassifier_InvalidLicenseSignatureOrFingerprint_ClassifiesAsLicenseInvalid()
    {
        var ex = new InvalidOperationException("Production license verification failed: machine fingerprint mismatch.");
        var result = StartupFailureClassifier.Classify(ex);

        Assert.Equal(StartupFailureCode.LICENSE_INVALID, result.Code);
        Assert.Contains(".erlic", result.RemediationGuidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartupFailureClassifier_GenericUnexpectedException_ClassifiesAsStartupInternalError()
    {
        var ex = new NotSupportedException("Some unanticipated system crash");
        var result = StartupFailureClassifier.Classify(ex);

        Assert.Equal(StartupFailureCode.STARTUP_INTERNAL_ERROR, result.Code);
    }

    [Fact]
    public void StartupFailureClassifier_ClassifyDatabaseFailure_HandlesPendingMigrations()
    {
        var migrations = new[] { "20260920_Init", "20260921_AddSales" };
        var result = StartupFailureClassifier.ClassifyDatabaseFailure("Pending migrations detected", migrations);

        Assert.Equal(StartupFailureCode.DATABASE_INCOMPATIBLE, result.Code);
        Assert.Contains("20260920_Init", result.ErrorMessage);
    }

    [Fact]
    public void StartupFailureClassifier_ClassifyDatabaseFailure_HandlesConnectivityLoss()
    {
        var result = StartupFailureClassifier.ClassifyDatabaseFailure("Failed to connect to localhost:5432", Array.Empty<string>());

        Assert.Equal(StartupFailureCode.DATABASE_UNAVAILABLE, result.Code);
        Assert.Contains("PostgreSQL", result.RemediationGuidance);
    }

    [Fact]
    public void BackendRuntime_ResolveConnectionString_FailsClosedWhenUnconfigured()
    {
        var prevDb = Environment.GetEnvironmentVariable("EDGE_RETAILS_DB");
        try
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_DB", null);
            // If neither ProgramData nor LocalAppData config exist, it should throw InvalidOperationException
            // and that exception must classify as CONFIGURATION_REQUIRED
            try
            {
                var connStr = BackendRuntime.ResolveConnectionString();
                // If a local config file exists on this machine, connStr is non-empty
                Assert.False(string.IsNullOrWhiteSpace(connStr));
            }
            catch (InvalidOperationException ex)
            {
                var classified = StartupFailureClassifier.Classify(ex);
                Assert.Equal(StartupFailureCode.CONFIGURATION_REQUIRED, classified.Code);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_DB", prevDb);
        }
    }

    [Fact]
    public async Task BackendSetupService_FailsClosed_WhenLicenseMissing()
    {
        var services = new ServiceCollection();
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        var setupService = new BackendSetupService(scopeFactory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            setupService.CompleteFirstSetupAsync("Shop", "Owner", "123", "Addr", "1234", "Retail", ""));

        Assert.Contains("valid signed Edge Retails license (.erlic) is required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BackendSetupService_FailsClosed_WhenLicenseSignatureInvalid()
    {
        var mockValidator = new MockLicenseValidator(new LicenseValidationResult(
            LicenseValidationStatus.InvalidSignature,
            null,
            "Cryptographic signature check failed."));
        var mockStore = new MockLicenseStore();

        var services = new ServiceCollection();
        services.AddScoped<EdgeRetails.Application.Production.Licensing.ILicenseValidator>(_ => mockValidator);
        services.AddScoped<EdgeRetails.Application.Production.Licensing.ILicenseStore>(_ => mockStore);

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        var setupService = new BackendSetupService(scopeFactory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            setupService.CompleteFirstSetupAsync("Shop", "Owner", "123", "Addr", "1234", "Retail", "tampered_license_content"));

        Assert.Contains("License verification failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(mockStore.PersistCalled);
    }

    [Fact]
    public void ArchitectureBoundary_DesktopViewModels_DoNotReferenceDbContextOrDirectGateways()
    {
        var vmAssembly = typeof(EdgeRetails.Desktop.ViewModels.MainViewModel).Assembly;
        var vmTypes = vmAssembly.GetTypes()
            .Where(t => t.Namespace != null && t.Namespace.StartsWith("EdgeRetails.Desktop.ViewModels"))
            .ToArray();

        foreach (var vmType in vmTypes)
        {
            // ViewModels must never reference DbContext
            var fields = vmType.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            foreach (var field in fields)
            {
                Assert.DoesNotContain("DbContext", field.FieldType.Name);
                Assert.DoesNotContain("LocalApplicationGateway", field.FieldType.Name);
                Assert.DoesNotContain("RemoteApplicationGateway", field.FieldType.Name);
            }
        }
    }

    [Fact]
    public void InstallerAndOperationalScripts_WorkerAndServerPaths_MatchPublishedLayout()
    {
        var baseDir = AppContext.BaseDirectory;
        // Search up for solution root
        var current = new DirectoryInfo(baseDir);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "EdgeRetails.sln")))
        {
            current = current.Parent;
        }

        if (current != null)
        {
            var guidePath = Path.Combine(current.FullName, "docs", "operations", "Installer_Deployment_Guide.md");
            if (File.Exists(guidePath))
            {
                var content = File.ReadAllText(guidePath);
                Assert.Contains(@"worker\EdgeRetails.Worker.exe", content);
                Assert.Contains(@"server\EdgeRetails.Server.exe", content);
                Assert.DoesNotContain(@"binPath= ""C:\Program Files\Edge Retails\EdgeRetails.Worker.exe""", content);
                Assert.DoesNotContain(@"binPath= ""C:\Program Files\Edge Retails\EdgeRetails.Server.exe""", content);
            }

            var registerScript = Path.Combine(current.FullName, "scripts", "Register-EdgeRetailsServices.ps1");
            Assert.True(File.Exists(registerScript), "Register-EdgeRetailsServices.ps1 must exist.");
            var scriptContent = File.ReadAllText(registerScript);
            Assert.Contains(@"worker\EdgeRetails.Worker.exe", scriptContent);
            Assert.Contains(@"server\EdgeRetails.Server.exe", scriptContent);
        }
    }

    private sealed class MockLicenseValidator : EdgeRetails.Application.Production.Licensing.ILicenseValidator
    {
        private readonly LicenseValidationResult _result;
        public MockLicenseValidator(LicenseValidationResult result) => _result = result;

        public Task<LicenseValidationResult> ValidateAsync(string rawLicense, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    private sealed class MockLicenseStore : EdgeRetails.Application.Production.Licensing.ILicenseStore
    {
        public bool PersistCalled { get; private set; }

        public Task<string?> ReadRawAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task PersistRawAsync(string rawLicense, CancellationToken cancellationToken = default)
        {
            PersistCalled = true;
            return Task.CompletedTask;
        }
        public Task<bool> TryPersistInitialRawAsync(string rawLicense, CancellationToken cancellationToken = default)
        {
            PersistCalled = true;
            return Task.FromResult(true);
        }
    }
}
