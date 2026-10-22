using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CentralDbContext))]
[Migration("20261022200000_DirectoryContactReferences")]
public sealed class DirectoryContactReferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("ContactReference", "customers", "text", schema: "central", nullable: true);
        migrationBuilder.AddColumn<string>("ContactReference", "shops", "text", schema: "central", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException("Persisted contact references require a reviewed forward migration; application rollback can retain these additive columns.");
    }
}
