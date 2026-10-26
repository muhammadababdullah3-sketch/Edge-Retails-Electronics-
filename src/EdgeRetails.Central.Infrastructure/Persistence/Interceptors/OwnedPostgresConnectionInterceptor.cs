using System.Data.Common;
using System.Globalization;
using EdgeRetails.Central.Application.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EdgeRetails.Central.Infrastructure.Persistence.Interceptors;

public sealed class OwnedPostgresConnectionInterceptor : DbConnectionInterceptor
{
    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    { new DatabaseTargetGuard().ValidateConnectionString(connection.ConnectionString); return result; }
    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    { new DatabaseTargetGuard().ValidateConnectionString(connection.ConnectionString); return ValueTask.FromResult(result); }
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Attest(connection);
    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    { Attest(connection); return Task.CompletedTask; }
    private static void Attest(DbConnection connection)
    {
        // Validate the local PostgreSQL process and its exact owned data directory on the host.
        // PostgreSQL restricts data_directory to pg_read_all_settings, a broad information role
        // inappropriate for the runtime role. The connection itself is verified independently.
        DatabaseTargetGuard.ValidateOwnedProcess();
        var expectedPort = int.Parse(DatabaseTargetGuard.Required("CENTRAL_PG_PORT"), CultureInfo.InvariantCulture);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT current_setting('server_version_num')::int, current_database(), current_user, (SELECT rolsuper FROM pg_roles WHERE rolname=current_user), host(inet_server_addr()), inet_server_port()";
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            throw new DatabaseTargetGuardException("LIVE_TARGET_MISMATCH", "Live PostgreSQL identity/role differs from owned target.");
        }

        var role = r.GetString(2);
        var serverAddress = r.IsDBNull(4) ? null : r.GetString(4);
        if (r.GetInt32(0) / 10000 != 18 || r.GetString(1) != DatabaseTargetGuard.Required("CENTRAL_PG_DATABASE") ||
            (role != DatabaseTargetGuard.Required("CENTRAL_PG_ROLE") && role != DatabaseTargetGuard.Required("CENTRAL_PG_MIGRATION_ROLE")) ||
            r.GetBoolean(3) || serverAddress is not ("127.0.0.1" or "::1") || r.GetInt32(5) != expectedPort)
        {
            throw new DatabaseTargetGuardException("LIVE_TARGET_MISMATCH", "Live PostgreSQL identity/role differs from owned target.");
        }
        var isRuntime = role == DatabaseTargetGuard.Required("CENTRAL_PG_ROLE");
        r.Close();
        if (isRuntime)
        {
            using var roles = connection.CreateCommand();
            roles.CommandText = """
                SELECT
                    EXISTS(SELECT 1 FROM pg_namespace WHERE nspname='central'),
                    EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='central' AND pg_get_userbyid(c.relowner)=current_user),
                    has_table_privilege(current_user, 'central.audit_records','UPDATE,DELETE,TRUNCATE'),
                    EXISTS(
                        SELECT 1
                        FROM pg_roles own_role
                        WHERE own_role.rolname=current_user
                          AND (own_role.rolsuper OR own_role.rolcreaterole OR own_role.rolcreatedb OR own_role.rolreplication OR own_role.rolbypassrls)
                    ) OR EXISTS(
                        WITH RECURSIVE role_closure(roleid) AS (
                            SELECT oid FROM pg_roles WHERE rolname=current_user
                            UNION
                            SELECT membership.roleid
                            FROM pg_auth_members membership
                            JOIN role_closure member_role ON membership.member=member_role.roleid
                        )
                        SELECT 1
                        FROM role_closure inherited
                        WHERE inherited.roleid <> (SELECT oid FROM pg_roles WHERE rolname=current_user)
                    )
                """;
            using var rr = roles.ExecuteReader();
            if (!rr.Read() || !rr.GetBoolean(0) || rr.GetBoolean(1) || rr.GetBoolean(2))
            {
                throw new DatabaseTargetGuardException("RUNTIME_ROLE_NOT_ISOLATED", "Runtime role must not own central tables or mutate audit evidence.");
            }
            if (rr.GetBoolean(3))
            {
                throw new DatabaseTargetGuardException("RUNTIME_ROLE_PRIVILEGE_ESCALATION", "Runtime role must have no elevated attributes or inherited role memberships.");
            }
        }

    }
}
