using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CentralDbContext))]
[Migration("20261021120000_SfrUploadSessionMetadata")]
public partial class SfrUploadSessionMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ContentType",
            schema: "central",
            table: "upload_sessions",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LocalManifestAuthenticationOpaque",
            schema: "central",
            table: "upload_sessions",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "RetentionPolicyId",
            schema: "central",
            table: "upload_sessions",
            type: "uuid",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ContentType", schema: "central", table: "upload_sessions");
        migrationBuilder.DropColumn(name: "LocalManifestAuthenticationOpaque", schema: "central", table: "upload_sessions");
        migrationBuilder.DropColumn(name: "RetentionPolicyId", schema: "central", table: "upload_sessions");
    }
}
