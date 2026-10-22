using Microsoft.EntityFrameworkCore.Migrations;

namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

public partial class CentralActivationDeliveryBinding : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "TokenPolicyId",
            schema: "central",
            table: "activation_tokens",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "DeliveryChannelId",
            schema: "central",
            table: "activation_tokens",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DeliveryReference",
            schema: "central",
            table: "activation_tokens",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DeliverySecretKeyId",
            schema: "central",
            table: "activation_tokens",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProtectedDeliverySecret",
            schema: "central",
            table: "activation_tokens",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DeliveryState",
            schema: "central",
            table: "activation_tokens",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.Sql(
            "UPDATE central.activation_tokens SET \"DeliveryState\" = 'NOT_CONFIGURED' WHERE \"DeliveryState\" IS NULL;");

        migrationBuilder.AlterColumn<string>(
            name: "DeliveryState",
            schema: "central",
            table: "activation_tokens",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(32)",
            oldMaxLength: 32,
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_activation_tokens_DeliveryReference",
            schema: "central",
            table: "activation_tokens",
            column: "DeliveryReference",
            unique: true,
            filter: "\"DeliveryReference\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_activation_tokens_DeliveryReference",
            schema: "central",
            table: "activation_tokens");

        migrationBuilder.DropColumn(name: "DeliveryChannelId", schema: "central", table: "activation_tokens");
        migrationBuilder.DropColumn(name: "DeliveryReference", schema: "central", table: "activation_tokens");
        migrationBuilder.DropColumn(name: "DeliverySecretKeyId", schema: "central", table: "activation_tokens");
        migrationBuilder.DropColumn(name: "DeliveryState", schema: "central", table: "activation_tokens");
        migrationBuilder.DropColumn(name: "ProtectedDeliverySecret", schema: "central", table: "activation_tokens");
        migrationBuilder.DropColumn(name: "TokenPolicyId", schema: "central", table: "activation_tokens");
    }
}
