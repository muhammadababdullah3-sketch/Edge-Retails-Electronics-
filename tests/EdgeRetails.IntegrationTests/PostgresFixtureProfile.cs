using System.Diagnostics;
using System.Text.RegularExpressions;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

internal enum PostgresFixtureKind { Phase11, Master, Phase1Model }
internal enum PostgresFixtureSchema { Migrated, ModelOnly, Historical }

/// <summary>Immutable, explicit fixture routing. Credentials are never included in diagnostics.</summary>
internal sealed class PostgresFixtureProfile
{
    public PostgresFixtureKind Kind { get; }
    public PostgresFixtureSchema Schema { get; }
    public string ConnectionString { get; }
    public string MaintenanceConnectionString { get; }
    public string BackupConnectionString { get; }
    public string OwnedRoot { get; }
    public string Database { get; }
    public string CustodyRoot => Path.Combine(OwnedRoot, "fixture-custody", Database);

    private PostgresFixtureProfile(PostgresFixtureKind kind, PostgresFixtureSchema schema,
        string connection, string maintenance, string root, string backup)
    {
        Kind = kind;
        Schema = schema;
        ConnectionString = connection;
        MaintenanceConnectionString = maintenance;
        BackupConnectionString = backup;
        OwnedRoot = Path.GetFullPath(root);
        var app = new NpgsqlConnectionStringBuilder(connection);
        var admin = new NpgsqlConnectionStringBuilder(maintenance);
        var read = new NpgsqlConnectionStringBuilder(backup);
        Database = app.Database ?? throw new InvalidOperationException("Fixture database must be explicit.");
        if (app.Host != "127.0.0.1" || admin.Host != app.Host || admin.Port != app.Port ||
            app.Port is < 55000 or > 55999 || app.Username != "er_provisioner" ||
            admin.Username != "er_master_admin" || admin.Database != "postgres" ||
            string.IsNullOrWhiteSpace(app.Password) || string.IsNullOrWhiteSpace(admin.Password))
        {
            throw new InvalidOperationException("Fixture endpoint or distinct write/maintenance identity is invalid.");
        }
        if (read.Host != app.Host || read.Port != app.Port || read.Database != app.Database ||
            read.Username != "er_master_runtime" || string.IsNullOrWhiteSpace(read.Password))
        {
            throw new InvalidOperationException("Fixture backup identity is invalid.");
        }
        if (schema != PostgresFixtureSchema.Historical &&
            (kind == PostgresFixtureKind.Phase11
                ? app.Port is not (55437 or 55438) || !Database.StartsWith("edgeretails_test_", StringComparison.Ordinal)
                : Database != "edge_retails_master_test"))
        {
            throw new InvalidOperationException("Fixture database does not match its named profile.");
        }
        if (schema == PostgresFixtureSchema.Historical &&
            !Regex.IsMatch(Database,
                @"^edge_retails_(tracking_rehearsal|warranty_migration|h02_history|master_restore|catalog_v3_production|catalog_loose_return|terminal_quota)_[a-f0-9]{32}$"))
        {
            throw new InvalidOperationException("Historical fixture database is not an owned generated name.");
        }
        if (!Directory.Exists(OwnedRoot) || (File.GetAttributes(OwnedRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Fixture requires an existing, non-redirected owned root.");
        }
    }

    public static PostgresFixtureProfile Resolve(PostgresFixtureKind kind = PostgresFixtureKind.Phase11)
    {
        string Require(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Explicit fixture setting {name} is required.");
        var prefix = kind switch
        {
            PostgresFixtureKind.Phase11 => "EDGE_RETAILS_FIXTURE_PHASE11",
            PostgresFixtureKind.Master => "EDGE_RETAILS_FIXTURE_MASTER",
            _ => "EDGE_RETAILS_FIXTURE_MODEL"
        };
        // Preserve the canonical explicit default connection for existing callers, never a file fallback.
        var connection = kind == PostgresFixtureKind.Phase11
            ? Environment.GetEnvironmentVariable(prefix + "_DB") ?? Require("EDGE_RETAILS_TEST_DB")
            : Require(prefix + "_DB");
        return new(kind, kind == PostgresFixtureKind.Phase1Model ? PostgresFixtureSchema.ModelOnly : PostgresFixtureSchema.Migrated,
            connection, Require(prefix + "_MAINT"), Require(prefix + "_ROOT"), Require(prefix + "_BACKUP"));
    }

    internal static PostgresFixtureProfile FromConfiguration(PostgresFixtureKind kind, string connection,
        string maintenance, string root, string backup) => new(kind,
        kind == PostgresFixtureKind.Phase1Model ? PostgresFixtureSchema.ModelOnly : PostgresFixtureSchema.Migrated,
        connection, maintenance, root, backup);

    public PostgresFixtureProfile ForOwnedDatabase(string database) => new(Kind, PostgresFixtureSchema.Historical,
        new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString,
        MaintenanceConnectionString, OwnedRoot,
        new NpgsqlConnectionStringBuilder(BackupConnectionString) { Database = database }.ConnectionString);

    public void Attest()
    {
        Phase2PostgresTestHarness.AttestOwnedPostgresRoot(ConnectionString, OwnedRoot);
        var pidFile = Path.Combine(OwnedRoot, "data", "postmaster.pid");
        var pid = File.ReadAllLines(pidFile);
        var connection = new NpgsqlConnectionStringBuilder(ConnectionString);
        if (pid.Length < 4 || !int.TryParse(pid[0], out var processId) ||
            !int.TryParse(pid[3], out var port) || port != connection.Port ||
            Path.GetFullPath(pid[1]) != Path.GetFullPath(Path.Combine(OwnedRoot, "data")))
        {
            throw new InvalidOperationException("Fixture PostgreSQL process/data directory binding is invalid.");
        }
        using var process = Process.GetProcessById(processId);
        if (!process.ProcessName.Equals("postgres", StringComparison.OrdinalIgnoreCase) || process.HasExited)
        {
            throw new InvalidOperationException("Fixture PostgreSQL process is not live.");
        }
        using var pg = new NpgsqlConnection(ConnectionString);
        pg.Open();
        using var identity = new NpgsqlCommand("""
            SELECT current_database(), current_user, inet_server_port(),
              EXISTS(SELECT 1 FROM pg_roles r WHERE pg_has_role(current_user,r.oid,'MEMBER')
                AND (r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls)),
              to_regclass('system.__ef_migrations_history') IS NOT NULL
            """, pg);
        using var row = identity.ExecuteReader();
        if (!row.Read() || row.GetString(0) != Database || row.GetString(1) != "er_provisioner" ||
            row.GetInt32(2) != connection.Port || row.GetBoolean(3) ||
            (Schema == PostgresFixtureSchema.ModelOnly ? row.GetBoolean(4) : !row.GetBoolean(4)))
        {
            throw new InvalidOperationException("Fixture role membership, database or schema mode failed attestation.");
        }
    }

    public NpgsqlConnection OpenMaintenance()
    {
        Attest();
        var pg = new NpgsqlConnection(MaintenanceConnectionString);
        try
        {
            pg.Open();
            using var cmd = new NpgsqlCommand("SELECT current_user, current_database(), inet_server_port(), current_setting('data_directory')", pg);
            using var row = cmd.ExecuteReader();
            if (!row.Read() || row.GetString(0) != "er_master_admin" || row.GetString(1) != "postgres" ||
                row.GetInt32(2) != new NpgsqlConnectionStringBuilder(ConnectionString).Port ||
                Path.GetFullPath(row.GetString(3)) != Path.GetFullPath(Path.Combine(OwnedRoot, "data")))
            {
                throw new InvalidOperationException("Maintenance connection does not match its attested owned cluster.");
            }
            return pg;
        }
        catch { pg.Dispose(); throw; }
    }

    public string MaintenanceForDatabase(string database)
    {
        _ = ForOwnedDatabase(database); // Validate the generated resource name before elevated routing.
        return new NpgsqlConnectionStringBuilder(MaintenanceConnectionString) { Database = database }.ConnectionString;
    }

    public void GrantWriteAccess(string database)
    {
        using var check = OpenMaintenance();
        using var pg = new NpgsqlConnection(MaintenanceForDatabase(database));
        pg.Open();
        using var grant = new NpgsqlCommand("""
            DO $$ DECLARE s record; BEGIN
              FOR s IN SELECT nspname FROM pg_namespace WHERE nspname NOT LIKE 'pg_%' AND nspname <> 'information_schema' LOOP
                EXECUTE format('GRANT USAGE ON SCHEMA %I TO er_provisioner, er_master_runtime',s.nspname);
                EXECUTE format('GRANT SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER ON ALL TABLES IN SCHEMA %I TO er_provisioner',s.nspname);
                EXECUTE format('GRANT USAGE,SELECT,UPDATE ON ALL SEQUENCES IN SCHEMA %I TO er_provisioner',s.nspname);
                EXECUTE format('GRANT SELECT ON ALL TABLES IN SCHEMA %I TO er_master_runtime',s.nspname);
                EXECUTE format('GRANT SELECT ON ALL SEQUENCES IN SCHEMA %I TO er_master_runtime',s.nspname);
              END LOOP;
            END $$;
            """, pg);
        grant.ExecuteNonQuery();
    }
}
