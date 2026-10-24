using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase12OperationOutcomeResponsePayload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE system.operation_outcomes SET payload_fingerprint = 'sha256:legacy_backfill' WHERE payload_fingerprint IS NULL;");

            migrationBuilder.AddColumn<string>(
                name: "response_content_type",
                schema: "system",
                table: "operation_outcomes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "response_payload",
                schema: "system",
                table: "operation_outcomes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "response_status_code",
                schema: "system",
                table: "operation_outcomes",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "response_content_type",
                schema: "system",
                table: "operation_outcomes");

            migrationBuilder.DropColumn(
                name: "response_payload",
                schema: "system",
                table: "operation_outcomes");

            migrationBuilder.DropColumn(
                name: "response_status_code",
                schema: "system",
                table: "operation_outcomes");
        }
    }
}
