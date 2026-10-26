using System.Security.Principal;
using System.Text.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace EdgeRetails.Infrastructure;

public static class GuardedTestSequenceAuthorityExtensions
{
    public static IServiceCollection AddGuardedTestSequenceAuthority(
        this IServiceCollection services,
        string connectionString,
        string? runnerRootOverride = null,
        bool skipLiveConnectionCheck = false)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string is required for guarded test sequence authority.", nameof(connectionString));
        }

        var connBuilder = new NpgsqlConnectionStringBuilder(connectionString);

        // Guard 1: Host must be loopback strictly (127.0.0.1 or localhost)
        if (connBuilder.Host != "127.0.0.1" && !string.Equals(connBuilder.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"FAIL-CLOSED: Host '{connBuilder.Host}' is prohibited. Only isolated loopback (127.0.0.1 or localhost) is permitted.");
        }

        // Guard 2: Port must be in isolated range (55000-65535) and strictly never port 5432
        if (connBuilder.Port == 5432)
        {
            throw new InvalidOperationException("FAIL-CLOSED: Port 5432 is prohibited. Connection to production or default cluster is forbidden.");
        }
        if (connBuilder.Port < 55000 || connBuilder.Port > 65535)
        {
            throw new InvalidOperationException($"FAIL-CLOSED: Port {connBuilder.Port} is outside the approved isolated range (55000-65535).");
        }

        // Guard 3: Database must be test database, strictly never production or default
        if (string.IsNullOrWhiteSpace(connBuilder.Database) ||
            connBuilder.Database.Equals("postgres", StringComparison.OrdinalIgnoreCase) ||
            connBuilder.Database.Equals("edgeretails", StringComparison.OrdinalIgnoreCase) ||
            connBuilder.Database.Contains("prod", StringComparison.OrdinalIgnoreCase) ||
            (!connBuilder.Database.StartsWith("edge_retails_", StringComparison.OrdinalIgnoreCase) &&
             !connBuilder.Database.StartsWith("edgeretails_test_", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"FAIL-CLOSED: Database '{connBuilder.Database}' is prohibited.");
        }

        // Guard 4: Role must be an approved disposable test role. Blanket postgres acceptance is strictly forbidden.
        if (string.IsNullOrWhiteSpace(connBuilder.Username) ||
            (!connBuilder.Username.Equals("er_provisioner", StringComparison.OrdinalIgnoreCase) &&
             !connBuilder.Username.Equals("er_runtime", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"FAIL-CLOSED: User '{connBuilder.Username}' is prohibited. Only approved disposable test roles (er_provisioner, er_runtime) are permitted.");
        }

        // Attest owned test runner root
        var (root, clusterRunId) = AttestClusterRoot(connBuilder, runnerRootOverride);

        // Optional Live DB Connection Attestation (executed when live database is expected)
        if (!skipLiveConnectionCheck)
        {
            AttestLiveCluster(connectionString, root);
        }

        // Authority directory resolution and binding
        var port = connBuilder.Port;
        var database = connBuilder.Database;
        string authorityRoot;
        var tempPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (root.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase))
        {
            authorityRoot = Path.Combine(root, "harness-highwater");
        }
        else
        {
            var runSuffix = !string.IsNullOrWhiteSpace(clusterRunId) ? $"_{clusterRunId}" : "";
            authorityRoot = Path.Combine(Path.GetTempPath(), $"EdgeRetailsMasterPg_{port}{runSuffix}", "harness-highwater");
        }

        var manifest = Path.Combine(authorityRoot, "highwater.manifest");
        var bindingFile = Path.Combine(authorityRoot, "cluster_binding.json");

        lock (SequenceFixtureCoordination.InitLock)
        {
            if (!Directory.Exists(authorityRoot))
            {
                Directory.CreateDirectory(authorityRoot);
                var newCustody = new OwnedSequenceAuthorityCustody(authorityRoot);
                MachineSequenceHighWaterService.InitializeOwnedFixture(manifest, newCustody);

                var bindingJson = JsonSerializer.Serialize(new
                {
                    ClusterRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    RunId = clusterRunId ?? "unknown",
                    Port = port,
                    Database = database,
                    BoundAtUtc = DateTimeOffset.UtcNow
                }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(bindingFile, bindingJson);
            }
            else
            {
                if (!File.Exists(bindingFile))
                {
                    SequenceFixtureCoordination.WaitForFile(bindingFile);
                }

                if (!File.Exists(bindingFile))
                {
                    throw new InvalidOperationException(
                        $"FAIL-CLOSED: Custody directory '{authorityRoot}' exists but is missing required 'cluster_binding.json'. Silently binding unverified existing authority is forbidden.");
                }

                try
                {
                    using var bindingDoc = JsonDocument.Parse(File.ReadAllText(bindingFile));
                    var boundRoot = bindingDoc.RootElement.TryGetProperty("ClusterRoot", out var br) ? br.GetString() : null;
                    var boundRunId = bindingDoc.RootElement.TryGetProperty("RunId", out var brid) ? brid.GetString() : null;
                    var boundPort = bindingDoc.RootElement.TryGetProperty("Port", out var bp) ? bp.GetInt32() : 0;
                    var boundDb = bindingDoc.RootElement.TryGetProperty("Database", out var bdb) ? bdb.GetString() : null;

                    var canonicalBoundRoot = !string.IsNullOrWhiteSpace(boundRoot)
                        ? Path.GetFullPath(boundRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        : null;
                    var canonicalCurrentRoot = !string.IsNullOrWhiteSpace(root)
                        ? Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        : null;

                    if (boundPort != port ||
                        !string.Equals(boundDb, database, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(canonicalBoundRoot, canonicalCurrentRoot, StringComparison.OrdinalIgnoreCase) ||
                        (clusterRunId is not null && (string.IsNullOrWhiteSpace(boundRunId) || !string.Equals(boundRunId, clusterRunId, StringComparison.OrdinalIgnoreCase))) ||
                        (clusterRunId is null && !string.IsNullOrWhiteSpace(boundRunId)))
                    {
                        throw new InvalidOperationException(
                            $"FAIL-CLOSED: Custody directory '{authorityRoot}' is bound to cluster root '{boundRoot}', run '{boundRunId}' (port {boundPort}, db '{boundDb}'), not current root '{root}', run '{clusterRunId}' (port {port}, db '{database}'). Cluster binding mismatch.");
                    }
                }
                catch (Exception ex) when (ex is not InvalidOperationException)
                {
                    throw new InvalidOperationException($"FAIL-CLOSED: Corrupt cluster binding file at '{bindingFile}'.", ex);
                }

                if (!SequenceFixtureCoordination.SequenceArtifactsComplete(manifest))
                {
                    SequenceFixtureCoordination.WaitForSequenceArtifacts(manifest);
                }

                if (!SequenceFixtureCoordination.SequenceArtifactsComplete(manifest))
                {
                    throw new InvalidOperationException("FAIL-CLOSED: Sequence fixture artifacts are incomplete or corrupt in existing authority root. Deleting or recreating legitimate authority is forbidden.");
                }
            }
        }

        var custody = new OwnedSequenceAuthorityCustody(authorityRoot);
        services.RemoveAll<ISequenceHighWaterService>();
        services.AddSingleton<ISequenceHighWaterService>(new MachineSequenceHighWaterService(manifest, custody));

        return services;
    }

    private static (string Root, string? ClusterRunId) AttestClusterRoot(NpgsqlConnectionStringBuilder connection, string? runnerRootOverride)
    {
        var rawEnvRoot = runnerRootOverride ?? Environment.GetEnvironmentVariable("EDGE_RETAILS_MASTER_PG_RUN_ROOT");
        string root;
        if (!string.IsNullOrWhiteSpace(rawEnvRoot))
        {
            root = Path.GetFullPath(rawEnvRoot);
        }
        else
        {
            var disposableDir = @"C:\Users\muham\EdgeRetailsDisposable\ER-RUN-20261011-005";
            if (Directory.Exists(disposableDir))
            {
                root = Path.GetFullPath(disposableDir);
            }
            else
            {
                throw new InvalidOperationException("FAIL-CLOSED: An owned PostgreSQL runner root is required.");
            }
        }

        var (_, clusterRunId) = OwnedPostgresRunnerAttestation.TryApproveRunnerRoot(root, connection.Port);
        OwnedPostgresRunnerAttestation.EnsureRunnerRootApproved(root, connection.Port);

        return (root, clusterRunId);
    }

    private static void AttestLiveCluster(string connectionString, string root)
    {
        using var pg = new NpgsqlConnection(connectionString);
        pg.Open();

        using var cmdLiveUser = new NpgsqlCommand("SELECT current_user;", pg);
        var liveUser = cmdLiveUser.ExecuteScalar()?.ToString() ?? "";
        if (!liveUser.Equals("er_provisioner", StringComparison.OrdinalIgnoreCase) &&
            !liveUser.Equals("er_runtime", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"FAIL-CLOSED: Live PostgreSQL session user '{liveUser}' is not an approved disposable test role.");
        }

        using var query = new NpgsqlCommand("SHOW data_directory", pg);
        var actual = query.ExecuteScalar() as string
            ?? throw new InvalidOperationException("FAIL-CLOSED: Unable to query data_directory.");

        var actualNormalized = Path.GetFullPath(actual);
        var expectedData = Path.GetFullPath(Path.Combine(root, "data"));
        var expectedPgData = Path.GetFullPath(Path.Combine(root, "pgdata"));

        if (!actualNormalized.Equals(expectedData, StringComparison.OrdinalIgnoreCase) &&
            !actualNormalized.Equals(expectedPgData, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"FAIL-CLOSED: PostgreSQL server data_directory '{actualNormalized}' does not match its owned runner root '{root}'.");
        }

        using var cmdVer = new NpgsqlCommand("SHOW server_version_num;", pg);
        var verStr = cmdVer.ExecuteScalar()?.ToString() ?? "0";
        if (!int.TryParse(verStr, out var verNum) || verNum < 180000 || verNum >= 190000)
        {
            throw new InvalidOperationException($"FAIL-CLOSED: Target PostgreSQL version {verStr} is not PostgreSQL 18.");
        }
    }
}
