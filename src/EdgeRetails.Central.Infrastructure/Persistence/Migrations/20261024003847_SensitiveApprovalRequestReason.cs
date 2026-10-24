using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class SensitiveApprovalRequestReason : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "RequestReason",
            schema: "central",
            table: "sensitive_approval_requests",
            type: "character varying(500)",
            maxLength: 500,
            nullable: false,
            defaultValue: "");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "RequestReason",
            schema: "central",
            table: "sensitive_approval_requests");
    }
}
