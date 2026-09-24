using System.Net.Sockets;
using EdgeRetails.Desktop.Services;
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
}
