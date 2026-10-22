using Microsoft.EntityFrameworkCore.Migrations;

namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

public partial class CentralActivationReviewDecision : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DecidedAtUtc",
            schema: "central",
            table: "activation_redemptions",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DecisionReasonCode",
            schema: "central",
            table: "activation_redemptions",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ReviewedByAdminId",
            schema: "central",
            table: "activation_redemptions",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "RowVersion",
            schema: "central",
            table: "activation_redemptions",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<string>(
            name: "ValidationSnapshotJson",
            schema: "central",
            table: "activation_redemptions",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ValidationSnapshotVersion",
            schema: "central",
            table: "activation_redemptions",
            type: "integer",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "DecidedAtUtc", schema: "central", table: "activation_redemptions");
        migrationBuilder.DropColumn(name: "DecisionReasonCode", schema: "central", table: "activation_redemptions");
        migrationBuilder.DropColumn(name: "ReviewedByAdminId", schema: "central", table: "activation_redemptions");
        migrationBuilder.DropColumn(name: "RowVersion", schema: "central", table: "activation_redemptions");
        migrationBuilder.DropColumn(name: "ValidationSnapshotJson", schema: "central", table: "activation_redemptions");
        migrationBuilder.DropColumn(name: "ValidationSnapshotVersion", schema: "central", table: "activation_redemptions");
    }
}
