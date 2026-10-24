using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase11DurablePostingPeriodLockAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "posting");

            migrationBuilder.CreateTable(
                name: "period_locks",
                schema: "posting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    period_start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    is_locked = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    locked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_by = table.Column<Guid>(type: "uuid", nullable: false),
                    lock_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_period_locks", x => x.id);
                    table.CheckConstraint("chk_period_lock_dates", "period_end_date >= period_start_date");
                });

            migrationBuilder.CreateTable(
                name: "posting_audit_entries",
                schema: "posting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_number = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supervisor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shop_date = table.Column<DateOnly>(type: "date", nullable: false),
                    posting_date = table.Column<DateOnly>(type: "date", nullable: false),
                    supplier_document_date = table.Column<DateOnly>(type: "date", nullable: true),
                    event_timestamp_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_posting_audit_entries", x => x.id);
                    table.CheckConstraint("chk_posting_audit_dates", "posting_date <= shop_date");
                });

            migrationBuilder.CreateIndex(
                name: "ix_period_locks_is_locked_period_start_date_period_end_date",
                schema: "posting",
                table: "period_locks",
                columns: new[] { "is_locked", "period_start_date", "period_end_date" });

            migrationBuilder.CreateIndex(
                name: "ix_posting_audit_entries_entity_id",
                schema: "posting",
                table: "posting_audit_entries",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_posting_audit_entries_operation_type",
                schema: "posting",
                table: "posting_audit_entries",
                column: "operation_type");

            migrationBuilder.CreateIndex(
                name: "ix_posting_audit_entries_posting_date",
                schema: "posting",
                table: "posting_audit_entries",
                column: "posting_date");

            migrationBuilder.CreateIndex(
                name: "ix_posting_audit_entries_shop_date",
                schema: "posting",
                table: "posting_audit_entries",
                column: "shop_date");

            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION posting.enforce_posting_audit_append_only()
RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'Posting audit entries are append-only ledger facts and cannot be modified or deleted (id: %)', OLD.id
        USING ERRCODE = '55000';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_posting_audit_append_only ON posting.posting_audit_entries;
CREATE TRIGGER trg_posting_audit_append_only
    BEFORE UPDATE OR DELETE ON posting.posting_audit_entries
    FOR EACH ROW
    EXECUTE FUNCTION posting.enforce_posting_audit_append_only();
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS trg_posting_audit_append_only ON posting.posting_audit_entries;
DROP FUNCTION IF EXISTS posting.enforce_posting_audit_append_only();
");

            migrationBuilder.DropTable(
                name: "period_locks",
                schema: "posting");

            migrationBuilder.DropTable(
                name: "posting_audit_entries",
                schema: "posting");
        }
    }
}
