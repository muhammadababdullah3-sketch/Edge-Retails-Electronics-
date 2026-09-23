using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase5WarrantyClaimClientOperationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "client_operation_id",
                schema: "warranty",
                table: "claims",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.CreateIndex(
                name: "ix_claims_client_operation_id",
                schema: "warranty",
                table: "claims",
                column: "client_operation_id",
                unique: true);

            // Historical rows need a generated backfill value, but new claims must
            // receive ClientOperationId from the Application/user-intent boundary.
            migrationBuilder.Sql(
                "ALTER TABLE warranty.claims ALTER COLUMN client_operation_id DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_claims_client_operation_id",
                schema: "warranty",
                table: "claims");

            migrationBuilder.DropColumn(
                name: "client_operation_id",
                schema: "warranty",
                table: "claims");
        }
    }
}
