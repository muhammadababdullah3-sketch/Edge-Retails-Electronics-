using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase8LifeIdentityHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE identity.user_sessions
                    ADD COLUMN IF NOT EXISTS last_activity_at timestamp with time zone NULL;
                UPDATE identity.user_sessions
                SET last_activity_at = started_at
                WHERE last_activity_at IS NULL;
                ALTER TABLE identity.user_sessions
                    ALTER COLUMN last_activity_at SET NOT NULL;

                ALTER TABLE finance.cash_sessions
                    ADD COLUMN IF NOT EXISTS void_reason character varying(500) NULL;
                ALTER TABLE finance.cash_sessions
                    ADD COLUMN IF NOT EXISTS voided_at timestamp with time zone NULL;
                ALTER TABLE finance.cash_sessions
                    ADD COLUMN IF NOT EXISTS voided_by uuid NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_activity_at",
                schema: "identity",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "void_reason",
                schema: "finance",
                table: "cash_sessions");

            migrationBuilder.DropColumn(
                name: "voided_at",
                schema: "finance",
                table: "cash_sessions");

            migrationBuilder.DropColumn(
                name: "voided_by",
                schema: "finance",
                table: "cash_sessions");
        }
    }
}
