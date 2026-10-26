using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase8CashSessionPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO identity.permissions (id, key, description, is_active)
                SELECT gen_random_uuid(), v.key, v.key, true
                FROM (VALUES ('cash.session.close'), ('cash.session.owner_void')) AS v(key)
                WHERE NOT EXISTS (
                    SELECT 1 FROM identity.permissions p WHERE p.key = v.key);

                INSERT INTO identity.role_permissions (id, role_id, permission_id)
                SELECT gen_random_uuid(), rp.role_id, p_close.id
                FROM identity.role_permissions rp
                INNER JOIN identity.permissions p_pos
                    ON p_pos.id = rp.permission_id AND p_pos.key = 'sales.pos.use'
                INNER JOIN identity.permissions p_close ON p_close.key = 'cash.session.close'
                WHERE NOT EXISTS (
                    SELECT 1 FROM identity.role_permissions existing
                    WHERE existing.role_id = rp.role_id AND existing.permission_id = p_close.id);

                INSERT INTO identity.role_permissions (id, role_id, permission_id)
                SELECT gen_random_uuid(), rp.role_id, p_void.id
                FROM identity.role_permissions rp
                INNER JOIN identity.permissions p_settings
                    ON p_settings.id = rp.permission_id AND p_settings.key = 'settings.manage'
                INNER JOIN identity.permissions p_void ON p_void.key = 'cash.session.owner_void'
                WHERE NOT EXISTS (
                    SELECT 1 FROM identity.role_permissions existing
                    WHERE existing.role_id = rp.role_id AND existing.permission_id = p_void.id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM identity.role_permissions rp
                USING identity.permissions p
                WHERE rp.permission_id = p.id
                  AND p.key IN ('cash.session.close', 'cash.session.owner_void');

                DELETE FROM identity.permissions
                WHERE key IN ('cash.session.close', 'cash.session.owner_void');
                """);
        }
    }
}
