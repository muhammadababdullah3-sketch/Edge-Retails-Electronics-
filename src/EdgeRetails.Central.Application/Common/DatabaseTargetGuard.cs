using System.Data.Common;

namespace EdgeRetails.Central.Application.Common;

public class DatabaseTargetGuard : IDatabaseTargetGuard
{
    private static readonly HashSet<int> StrictlyForbiddenPorts = new() { 5432, 7150 };
    private static readonly HashSet<string> StrictlyForbiddenDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "edgeretails", "edgeretails_pos", "shop_pos", "postgres", "template1", "edgeretails_dev"
    };

    public void ValidateConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new DatabaseTargetGuardException("CONNECTION_STRING_EMPTY", "Database connection string cannot be empty.");
        }

        var builder = new DbConnectionStringBuilder
        {
            ConnectionString = connectionString
        };

        var host = GetValueOrDefault(builder, "Host", "Server");
        var portStr = GetValueOrDefault(builder, "Port");
        var database = GetValueOrDefault(builder, "Database");
        var username = GetValueOrDefault(builder, "Username", "User Id");

        if (!int.TryParse(portStr, out var port))
        {
            port = 5432; // Default if not specified, which is strictly rejected
        }

        ValidateRuntimeTarget(host, port, database, username);
    }

    public void ValidateRuntimeTarget(string host, int port, string database, string username)
    {
        if (StrictlyForbiddenPorts.Contains(port))
        {
            throw new DatabaseTargetGuardException(
                "FORBIDDEN_PORT_TARGET",
                $"Target port {port} is strictly rejected by Central fail-closed guard (rejection of 5432 and 7150). Dedicated disposable port required.");
        }

        if (port < 1024 || port > 65535)
        {
            throw new DatabaseTargetGuardException(
                "INVALID_PORT_RANGE",
                $"Target port {port} is out of valid non-privileged disposable port range.");
        }

        var normalizedHost = host?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedHost != "127.0.0.1" && normalizedHost != "localhost" && normalizedHost != "::1")
        {
            throw new DatabaseTargetGuardException(
                "NON_LOOPBACK_TARGET",
                $"Target host '{host}' is forbidden. Central disposable test database must bind exclusively to loopback interface.");
        }

        if (string.IsNullOrWhiteSpace(database))
        {
            throw new DatabaseTargetGuardException(
                "DATABASE_NAME_REQUIRED",
                "Database name is required and cannot be empty.");
        }

        if (StrictlyForbiddenDatabases.Contains(database))
        {
            throw new DatabaseTargetGuardException(
                "FORBIDDEN_DATABASE_TARGET",
                $"Database '{database}' is strictly forbidden by Central guard to prevent collision with shop or default databases.");
        }

        var expectedPort = Required("CENTRAL_PG_PORT");
        var expectedDb = Required("CENTRAL_PG_DATABASE");
        var runtimeRole = Required("CENTRAL_PG_ROLE");
        var migrationRole = Required("CENTRAL_PG_MIGRATION_ROLE");
        if (port.ToString() != expectedPort || database != expectedDb ||
            (username != runtimeRole && username != migrationRole))
        {
            throw new DatabaseTargetGuardException("UNOWNED_TARGET", "Target differs from reserved owned PostgreSQL identity.");
        }
        ValidateOwnedProcess();

    }

    public void VerifyDataDirMarker(string markerFilePath)
    {
        if (string.IsNullOrWhiteSpace(markerFilePath))
        {
            throw new DatabaseTargetGuardException("MARKER_PATH_REQUIRED", "Data directory marker path is required.");
        }

        if (!File.Exists(markerFilePath))
        {
            throw new DatabaseTargetGuardException(
                "DATA_DIR_NOT_OWNED",
                $"Ownership attestation marker file was not found at '{markerFilePath}'. Target database cluster is not an authorized disposable test instance.");
        }

        var lines = File.ReadAllLines(markerFilePath);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var parts = line.Split('=', 2);
            if (parts.Length == 2)
            {
                map[parts[0].Trim()] = parts[1].Trim();
            }
        }

        if (!map.TryGetValue("cluster_type", out var clusterType) || clusterType != "disposable_pg18")
        {
            throw new DatabaseTargetGuardException(
                "INVALID_CLUSTER_TYPE",
                $"Attestation marker cluster_type '{clusterType}' is not authorized. Must be 'disposable_pg18'.");
        }

        if (!map.TryGetValue("owner", out var owner) || owner != "central_test_admin")
        {
            throw new DatabaseTargetGuardException(
                "INVALID_CLUSTER_OWNER",
                $"Attestation marker owner '{owner}' is not authorized. Must be 'central_test_admin'.");
        }
    }

    public async Task VerifyServerIdentityAsync(
        DbConnection connection,
        string expectedDatabase,
        string expectedUser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version(), current_database(), current_user;";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new DatabaseTargetGuardException("SERVER_IDENTITY_PROBE_FAILED", "Unable to probe PostgreSQL server identity.");
        }

        var versionStr = reader.GetString(0);
        var currentDb = reader.GetString(1);
        var currentUser = reader.GetString(2);

        if (!versionStr.Contains("PostgreSQL 18", StringComparison.OrdinalIgnoreCase))
        {
            throw new DatabaseTargetGuardException(
                "UNSUPPORTED_ENGINE_VERSION",
                $"Connected server engine '{versionStr}' is not PostgreSQL 18. Central target requires PostgreSQL 18.");
        }

        if (!string.Equals(currentDb, expectedDatabase, StringComparison.OrdinalIgnoreCase))
        {
            throw new DatabaseTargetGuardException(
                "DATABASE_MISMATCH",
                $"Connected database '{currentDb}' does not match expected disposable database '{expectedDatabase}'.");
        }

        if (!string.Equals(currentUser, expectedUser, StringComparison.OrdinalIgnoreCase))
        {
            throw new DatabaseTargetGuardException(
                "USER_ROLE_MISMATCH",
                $"Connected user role '{currentUser}' does not match expected role '{expectedUser}'.");
        }
    }

    public static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new DatabaseTargetGuardException("OWNERSHIP_CONFIGURATION_REQUIRED", $"Explicit {name} is required.");

    public static void ValidateOwnedProcess()
    {
        var pid = int.Parse(Required("CENTRAL_PG_PID"));
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        var binary = Path.GetFullPath(Required("CENTRAL_PG_BINARY"));
        if (process.HasExited || !string.Equals(Path.GetFullPath(process.MainModule!.FileName!), binary, StringComparison.OrdinalIgnoreCase))
        {
            throw new DatabaseTargetGuardException("PROCESS_MISMATCH", "Owned PostgreSQL process image differs.");
        }

        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(binary);
        if (version.FileMajorPart != 18)
        {
            throw new DatabaseTargetGuardException("ENGINE_MISMATCH", "PostgreSQL18 is required.");
        }

        using var marker = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Required("CENTRAL_PG_OWNERSHIP_MARKER")));
        var m = marker.RootElement;
        if (m.GetProperty("pid").GetInt32() != pid ||
            !string.Equals(Path.GetFullPath(m.GetProperty("dataDirectory").GetString()!), Path.GetFullPath(Required("CENTRAL_PG_DATA_DIR")), StringComparison.OrdinalIgnoreCase) ||
            m.GetProperty("port").GetInt32().ToString() != Required("CENTRAL_PG_PORT") ||
            m.GetProperty("database").GetString() != Required("CENTRAL_PG_DATABASE") ||
            m.GetProperty("runtimeRole").GetString() != Required("CENTRAL_PG_ROLE") ||
            m.GetProperty("migrationRole").GetString() != Required("CENTRAL_PG_MIGRATION_ROLE"))
        {
            throw new DatabaseTargetGuardException("OWNERSHIP_MARKER_MISMATCH", "Owned cluster marker differs from reserved identity.");
        }

        var postmaster = File.ReadAllLines(Path.Combine(Required("CENTRAL_PG_DATA_DIR"), "postmaster.pid"));
        if (postmaster.Length < 4 || postmaster[0] != pid.ToString() || postmaster[3] != Required("CENTRAL_PG_PORT") ||
            !string.Equals(Path.GetFullPath(postmaster[1]), Path.GetFullPath(Required("CENTRAL_PG_DATA_DIR")), StringComparison.OrdinalIgnoreCase))
        {
            throw new DatabaseTargetGuardException("POSTMASTER_MISMATCH", "Data directory does not identify the reserved running process.");
        }
    }

    private static string GetValueOrDefault(DbConnectionStringBuilder builder, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (builder.TryGetValue(key, out var val) && val is not null)
            {
                return val.ToString() ?? string.Empty;
            }
        }
        return string.Empty;
    }
}
