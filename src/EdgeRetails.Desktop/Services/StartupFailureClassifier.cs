using System.Net.Sockets;

namespace EdgeRetails.Desktop.Services;

public enum StartupFailureCode
{
    CONFIGURATION_REQUIRED,
    DATABASE_UNAVAILABLE,
    DATABASE_INCOMPATIBLE,
    DEPENDENCY_GRAPH_INVALID,
    LICENSE_CONFIGURATION_INVALID,
    LICENSE_INVALID,
    STARTUP_INTERNAL_ERROR
}

public sealed record StartupClassificationResult(
    StartupFailureCode Code,
    string Title,
    string ErrorMessage,
    string RemediationGuidance);

public static class StartupFailureClassifier
{
    public static StartupClassificationResult Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var current = exception;
        while (current is not null)
        {
            var message = current.Message;
            var typeName = current.GetType().FullName ?? string.Empty;

            // 1. Missing or unconfigured database connection / config
            if (message.Contains("EDGE_RETAILS_DB", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("no persistent configuration file was found", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("No database connection string configured", StringComparison.OrdinalIgnoreCase))
            {
                return new StartupClassificationResult(
                    StartupFailureCode.CONFIGURATION_REQUIRED,
                    "Database Configuration Required",
                    message,
                    "Configure the production database connection string via the 'EDGE_RETAILS_DB' environment variable or in '%ProgramData%\\EdgeRetails\\config.json'.");
            }

            // 2. License Configuration Invalid (missing/corrupt public key, private key rejected)
            if (message.Contains("EDGE_RETAILS_LICENSE_PUBLIC_KEY", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("license.pub", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("public key", StringComparison.OrdinalIgnoreCase) && message.Contains("license", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Vendor master license public key is unconfigured", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Private key material is forbidden", StringComparison.OrdinalIgnoreCase))
            {
                return new StartupClassificationResult(
                    StartupFailureCode.LICENSE_CONFIGURATION_INVALID,
                    "License Configuration Invalid",
                    message,
                    "Ensure the vendor master RSA public key is configured via 'EDGE_RETAILS_LICENSE_PUBLIC_KEY' or installed in '%ProgramData%\\EdgeRetails\\keys\\license.pub'. Private keys are strictly rejected.");
            }

            // 3. License Invalid (signature invalid, fingerprint mismatch, expired, corrupted payload)
            if (message.Contains("license", StringComparison.OrdinalIgnoreCase) &&
                (message.Contains("signature", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("tamper", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("fingerprint", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("machine", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("payload", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("not found", StringComparison.OrdinalIgnoreCase)))
            {
                return new StartupClassificationResult(
                    StartupFailureCode.LICENSE_INVALID,
                    "Production License Verification Failed",
                    message,
                    "A valid signed Edge Retails license file (*.erlic) matching this machine's hardware fingerprint is required. Complete the first-run setup wizard or place the license file in '%ProgramData%\\EdgeRetails\\license.erlic'.");
            }

            // 4. Dependency Graph Invalid (DI resolution failures)
            if (typeName.Contains("DependencyInjection", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Unable to resolve service", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("No service for type", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("A circular dependency was detected", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Cannot resolve scoped service", StringComparison.OrdinalIgnoreCase))
            {
                return new StartupClassificationResult(
                    StartupFailureCode.DEPENDENCY_GRAPH_INVALID,
                    "Dependency Graph Error",
                    message,
                    "A required service could not be resolved from the dependency injection container. Verify production service registrations and service lifetimes.");
            }

            // 5. Database Unavailable (socket, timeout, connection refused, authentication)
            if (current is SocketException ||
                typeName.StartsWith("Npg", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("Postgres", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("password authentication failed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Timeout", StringComparison.OrdinalIgnoreCase) && message.Contains("connect", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("The host was not found", StringComparison.OrdinalIgnoreCase))
            {
                return new StartupClassificationResult(
                    StartupFailureCode.DATABASE_UNAVAILABLE,
                    "Database Server Unavailable",
                    message,
                    "Ensure PostgreSQL 18 service is running on the host specified in the connection string, port 5432 is reachable, and the database credentials are valid.");
            }

            // 6. Database Incompatible (pending migrations, schema drift)
            if (message.Contains("migration", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("pending model changes", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("DatabaseIncompatible", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("column does not exist", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("relation does not exist", StringComparison.OrdinalIgnoreCase))
            {
                return new StartupClassificationResult(
                    StartupFailureCode.DATABASE_INCOMPATIBLE,
                    "Database Schema Incompatible",
                    message,
                    "The database schema is not compatible with this release of Edge Retails. Run pending EF Core migrations or execute the database upgrade script before starting.");
            }

            current = current.InnerException;
        }

        return new StartupClassificationResult(
            StartupFailureCode.STARTUP_INTERNAL_ERROR,
            "Internal Startup Error",
            exception.Message,
            "An unexpected internal error occurred during startup. Review Windows Event Log or the application log files in '%ProgramData%\\EdgeRetails\\logs'.");
    }

    public static StartupClassificationResult ClassifyDatabaseFailure(
        string? failureReason,
        IReadOnlyList<string>? pendingMigrations)
    {
        if (pendingMigrations is not null && pendingMigrations.Count > 0)
        {
            var migrationList = string.Join(", ", pendingMigrations);
            return new StartupClassificationResult(
                StartupFailureCode.DATABASE_INCOMPATIBLE,
                "Pending Database Migrations Required",
                $"Database is missing required migrations: {migrationList}",
                "Apply the pending migrations using the EdgeRetails Server migration command or 'dotnet ef database update' before launching the desktop client.");
        }

        var reason = failureReason ?? "Database readiness check failed.";

        if (reason.Contains("migration", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("schema", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("version", StringComparison.OrdinalIgnoreCase))
        {
            return new StartupClassificationResult(
                StartupFailureCode.DATABASE_INCOMPATIBLE,
                "Database Incompatible",
                reason,
                "Apply the required database migrations or run the database upgrade utility.");
        }

        if (reason.Contains("license", StringComparison.OrdinalIgnoreCase))
        {
            return new StartupClassificationResult(
                StartupFailureCode.LICENSE_INVALID,
                "License Verification Required",
                reason,
                "Install a valid production license (*.erlic) matching this machine fingerprint.");
        }

        if (reason.Contains("config", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("EDGE_RETAILS_DB", StringComparison.OrdinalIgnoreCase))
        {
            return new StartupClassificationResult(
                StartupFailureCode.CONFIGURATION_REQUIRED,
                "Configuration Required",
                reason,
                "Configure EDGE_RETAILS_DB or provide a valid config.json file.");
        }

        return new StartupClassificationResult(
            StartupFailureCode.DATABASE_UNAVAILABLE,
            "Database Connectivity Failure",
            reason,
            "Ensure the PostgreSQL 18 service is running, accessible over the network, and accepting connections for the configured user.");
    }
}
