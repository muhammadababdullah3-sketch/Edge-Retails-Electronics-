using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CentralDbContext))]
[Migration("20261024010000_CanonicalHardwareTransferDecisions")]
public partial class CanonicalHardwareTransferDecisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "ReportedMachineFingerprintId", table: "hardware_reviews", schema: "central", type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()");
        migrationBuilder.AddColumn<Guid>(name: "StoredMachineFingerprintId", table: "hardware_reviews", schema: "central", type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()");
        migrationBuilder.AddColumn<string>(name: "ContractDecision", table: "hardware_reviews", schema: "central", type: "character varying(32)", maxLength: 32, nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "VerificationEvidenceId", table: "hardware_reviews", schema: "central", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>(name: "ContractDecision", table: "primary_transfers", schema: "central", type: "character varying(32)", maxLength: 32, nullable: true);
        migrationBuilder.AddColumn<string>(name: "RecoveryType", table: "primary_transfers", schema: "central", type: "character varying(32)", maxLength: 32, nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "VerificationEvidenceId", table: "primary_transfers", schema: "central", type: "uuid", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ReportedMachineFingerprintId", table: "hardware_reviews", schema: "central");
        migrationBuilder.DropColumn(name: "StoredMachineFingerprintId", table: "hardware_reviews", schema: "central");
        migrationBuilder.DropColumn(name: "ContractDecision", table: "hardware_reviews", schema: "central");
        migrationBuilder.DropColumn(name: "VerificationEvidenceId", table: "hardware_reviews", schema: "central");
        migrationBuilder.DropColumn(name: "ContractDecision", table: "primary_transfers", schema: "central");
        migrationBuilder.DropColumn(name: "RecoveryType", table: "primary_transfers", schema: "central");
        migrationBuilder.DropColumn(name: "VerificationEvidenceId", table: "primary_transfers", schema: "central");
    }
}
