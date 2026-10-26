using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CentralDbContext))]
[Migration("20261022220000_PersistSupportAssignment")]
public sealed class PersistSupportAssignment : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "AssignedAdminUserId",
            schema: "central",
            table: "support_tickets",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_support_tickets_AssignedAdminUserId",
            schema: "central",
            table: "support_tickets",
            column: "AssignedAdminUserId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_support_tickets_AssignedAdminUserId", schema: "central", table: "support_tickets");
        migrationBuilder.DropColumn(name: "AssignedAdminUserId", schema: "central", table: "support_tickets");
    }
}
