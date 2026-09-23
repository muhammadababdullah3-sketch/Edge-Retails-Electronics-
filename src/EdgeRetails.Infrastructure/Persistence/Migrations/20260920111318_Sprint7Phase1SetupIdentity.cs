using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class Sprint7Phase1SetupIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "identity");

        migrationBuilder.CreateTable(
            name: "installation_state",
            schema: "system",
            columns: table => new
            {
                installation_id = table.Column<Guid>(type: "uuid", nullable: false),
                setup_status = table.Column<int>(type: "integer", nullable: false),
                selected_module = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                version = table.Column<long>(type: "bigint", nullable: false),
                singleton_key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "PRIMARY")
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_installation_state", x => x.installation_id);
                table.CheckConstraint("ck_installation_state_singleton", "singleton_key = 'PRIMARY'");
            });

        migrationBuilder.CreateTable(
            name: "permissions",
            schema: "identity",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                is_active = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_permissions", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "roles",
            schema: "identity",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                is_system = table.Column<bool>(type: "boolean", nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_roles", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "role_permissions",
            schema: "identity",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                role_id = table.Column<Guid>(type: "uuid", nullable: false),
                permission_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_role_permissions", x => x.id);
                table.ForeignKey(
                    name: "fk_role_permissions_permissions_permission_id",
                    column: x => x.permission_id,
                    principalSchema: "identity",
                    principalTable: "permissions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_role_permissions_roles_role_id",
                    column: x => x.role_id,
                    principalSchema: "identity",
                    principalTable: "roles",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "users",
            schema: "identity",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                display_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                role_id = table.Column<Guid>(type: "uuid", nullable: false),
                pin_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                pin_salt = table.Column<byte[]>(type: "bytea", nullable: false),
                pin_iterations = table.Column<int>(type: "integer", nullable: false),
                pin_algorithm = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                status = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_users", x => x.id);
                table.CheckConstraint("ck_user_pin_iterations_positive", "pin_iterations > 0");
                table.ForeignKey(
                    name: "fk_users_roles_role_id",
                    column: x => x.role_id,
                    principalSchema: "identity",
                    principalTable: "roles",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "user_permission_overrides",
            schema: "identity",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                permission_id = table.Column<Guid>(type: "uuid", nullable: false),
                is_allowed = table.Column<bool>(type: "boolean", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_user_permission_overrides", x => x.id);
                table.ForeignKey(
                    name: "fk_user_permission_overrides_permissions_permission_id",
                    column: x => x.permission_id,
                    principalSchema: "identity",
                    principalTable: "permissions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_user_permission_overrides_users_user_id",
                    column: x => x.user_id,
                    principalSchema: "identity",
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "user_sessions",
            schema: "identity",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                client_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                is_revoked = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_user_sessions", x => x.id);
                table.ForeignKey(
                    name: "fk_user_sessions_users_user_id",
                    column: x => x.user_id,
                    principalSchema: "identity",
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_installation_state_singleton_key",
            schema: "system",
            table: "installation_state",
            column: "singleton_key",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_permissions_key",
            schema: "identity",
            table: "permissions",
            column: "key",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_role_permissions_permission_id",
            schema: "identity",
            table: "role_permissions",
            column: "permission_id");

        migrationBuilder.CreateIndex(
            name: "ix_role_permissions_role_id_permission_id",
            schema: "identity",
            table: "role_permissions",
            columns: new[] { "role_id", "permission_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_roles_name",
            schema: "identity",
            table: "roles",
            column: "name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_user_permission_overrides_permission_id",
            schema: "identity",
            table: "user_permission_overrides",
            column: "permission_id");

        migrationBuilder.CreateIndex(
            name: "ix_user_permission_overrides_user_id_permission_id",
            schema: "identity",
            table: "user_permission_overrides",
            columns: new[] { "user_id", "permission_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_user_sessions_client_session_id",
            schema: "identity",
            table: "user_sessions",
            column: "client_session_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_user_sessions_user_id_ended_at",
            schema: "identity",
            table: "user_sessions",
            columns: new[] { "user_id", "ended_at" });

        migrationBuilder.CreateIndex(
            name: "ix_users_role_id",
            schema: "identity",
            table: "users",
            column: "role_id");

        migrationBuilder.CreateIndex(
            name: "ix_users_status_display_name",
            schema: "identity",
            table: "users",
            columns: new[] { "status", "display_name" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "installation_state",
            schema: "system");

        migrationBuilder.DropTable(
            name: "role_permissions",
            schema: "identity");

        migrationBuilder.DropTable(
            name: "user_permission_overrides",
            schema: "identity");

        migrationBuilder.DropTable(
            name: "user_sessions",
            schema: "identity");

        migrationBuilder.DropTable(
            name: "permissions",
            schema: "identity");

        migrationBuilder.DropTable(
            name: "users",
            schema: "identity");

        migrationBuilder.DropTable(
            name: "roles",
            schema: "identity");
    }
}
