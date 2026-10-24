using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase11ThakaCreditRefundTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "credit_refunds",
                schema: "thaka",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    refund_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    refunded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    refunded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credit_refunds", x => x.id);
                    table.CheckConstraint("ck_thaka_credit_refund_amount_positive", "amount > 0");
                    table.ForeignKey(
                        name: "fk_credit_refunds_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "thaka",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_credit_refunds_client_operation_id",
                schema: "thaka",
                table: "credit_refunds",
                column: "client_operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_credit_refunds_project_id_refunded_at",
                schema: "thaka",
                table: "credit_refunds",
                columns: new[] { "project_id", "refunded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_credit_refunds_refund_number",
                schema: "thaka",
                table: "credit_refunds",
                column: "refund_number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "credit_refunds",
                schema: "thaka");
        }
    }
}
