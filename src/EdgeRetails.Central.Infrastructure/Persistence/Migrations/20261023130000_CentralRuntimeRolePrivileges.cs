using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;
/// <summary>
/// Applies the existing Central runtime role contract after the migration-owned schema is complete.
/// The runtime role can use and mutate business tables, but cannot own schema objects or mutate audit evidence.
/// </summary>
public partial class CentralRuntimeRolePrivileges : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var (runtimeRoleName, migrationRoleName) = ReadRoles();
        var runtimeRole = QuoteIdentifier(runtimeRoleName);
        var migrationRole = QuoteIdentifier(migrationRoleName);
        migrationBuilder.Sql($"""
            DO $central_runtime_privileges$
            BEGIN
                IF current_user <> '{migrationRoleName}' THEN
                    RAISE EXCEPTION 'CENTRAL_RUNTIME_PRIVILEGES_REQUIRE_MIGRATION_ROLE';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{runtimeRoleName}') THEN
                    RAISE EXCEPTION 'CENTRAL_RUNTIME_ROLE_NOT_FOUND';
                END IF;
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{runtimeRoleName}' AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolreplication OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'CENTRAL_RUNTIME_ROLE_IS_ELEVATED';
                END IF;
                IF EXISTS (
                    WITH RECURSIVE inherited_roles(roleid) AS (
                        SELECT oid FROM pg_roles WHERE rolname = '{runtimeRoleName}'
                        UNION
                        SELECT membership.roleid
                        FROM pg_auth_members membership
                        JOIN inherited_roles member_role ON membership.member = member_role.roleid
                    )
                    SELECT 1
                    FROM inherited_roles inherited
                    JOIN pg_roles role_info ON role_info.oid = inherited.roleid
                    WHERE role_info.rolname = '{migrationRoleName}'
                ) THEN
                    RAISE EXCEPTION 'CENTRAL_RUNTIME_ROLE_INHERITS_MIGRATION_AUTHORITY';
                END IF;
                IF EXISTS (
                    WITH RECURSIVE inherited_roles(roleid) AS (
                        SELECT oid FROM pg_roles WHERE rolname = '{runtimeRoleName}'
                        UNION
                        SELECT membership.roleid
                        FROM pg_auth_members membership
                        JOIN inherited_roles member_role ON membership.member = member_role.roleid
                    )
                    SELECT 1
                    FROM inherited_roles inherited
                    WHERE inherited.roleid <> (SELECT oid FROM pg_roles WHERE rolname = '{runtimeRoleName}')
                ) THEN
                    RAISE EXCEPTION 'CENTRAL_RUNTIME_ROLE_HAS_INHERITED_MEMBERSHIP';
                END IF;
            END
            $central_runtime_privileges$;

            REVOKE ALL PRIVILEGES ON SCHEMA central FROM {runtimeRole};
            GRANT USAGE ON SCHEMA central TO {runtimeRole};
            REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA central FROM {runtimeRole};
            GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA central TO {runtimeRole};
            GRANT DELETE ON central.object_download_grants TO {runtimeRole};
            GRANT DELETE ON central.installation_request_nonces TO {runtimeRole};
            REVOKE UPDATE ON central.installation_request_nonces FROM {runtimeRole};
            REVOKE UPDATE, DELETE, TRUNCATE ON central.audit_records FROM {runtimeRole};
            REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA central FROM {runtimeRole};
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA central TO {runtimeRole};

            ALTER DEFAULT PRIVILEGES FOR ROLE {migrationRole} IN SCHEMA central REVOKE ALL ON TABLES FROM {runtimeRole};
            ALTER DEFAULT PRIVILEGES FOR ROLE {migrationRole} IN SCHEMA central GRANT SELECT, INSERT, UPDATE ON TABLES TO {runtimeRole};
            ALTER DEFAULT PRIVILEGES FOR ROLE {migrationRole} IN SCHEMA central REVOKE ALL ON SEQUENCES FROM {runtimeRole};
            ALTER DEFAULT PRIVILEGES FOR ROLE {migrationRole} IN SCHEMA central GRANT USAGE, SELECT ON SEQUENCES TO {runtimeRole};
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        var (runtimeRoleName, migrationRoleName) = ReadRoles();
        var runtimeRole = QuoteIdentifier(runtimeRoleName);
        var migrationRole = QuoteIdentifier(migrationRoleName);
        migrationBuilder.Sql($"""
            ALTER DEFAULT PRIVILEGES FOR ROLE {migrationRole} IN SCHEMA central REVOKE ALL ON TABLES FROM {runtimeRole};
            ALTER DEFAULT PRIVILEGES FOR ROLE {migrationRole} IN SCHEMA central REVOKE ALL ON SEQUENCES FROM {runtimeRole};
            REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA central FROM {runtimeRole};
            REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA central FROM {runtimeRole};
            REVOKE ALL PRIVILEGES ON SCHEMA central FROM {runtimeRole};
            """);
    }

    private static (string RuntimeRole, string MigrationRole) ReadRoles()
    {
        var runtimeRole = ReadRole("CENTRAL_PG_ROLE");
        var migrationRole = ReadRole("CENTRAL_PG_MIGRATION_ROLE");
        if (string.Equals(runtimeRole, migrationRole, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CENTRAL_PG_ROLE and CENTRAL_PG_MIGRATION_ROLE must identify separate roles.");
        }

        return (runtimeRole, migrationRole);
    }

    private static string ReadRole(string name)
    {
        var role = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(role) || !Regex.IsMatch(role, @"\A[a-z_][a-z0-9_]{0,62}\z", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException($"A valid explicit {name} is required for Central runtime privilege provisioning.");
        }

        return role;
    }

    private static string QuoteIdentifier(string role) => $"\"{role.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
