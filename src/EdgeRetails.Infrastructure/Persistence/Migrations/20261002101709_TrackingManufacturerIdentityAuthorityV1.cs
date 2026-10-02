using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrackingManufacturerIdentityAuthorityV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "unit_identity_claims",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    identifier_type = table.Column<int>(type: "integer", nullable: false),
                    identifier_slot = table.Column<int>(type: "integer", nullable: false),
                    raw_value = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    normalized_value = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    normalization_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit_identity_claims", x => x.id);
                    table.CheckConstraint("ck_inventory_unit_identity_claim_normalization_version", "normalization_version >= 1");
                    table.CheckConstraint("ck_inventory_unit_identity_claim_type_slot", "(identifier_type = 1 AND identifier_slot = 1) OR (identifier_type = 2 AND identifier_slot IN (2, 3))");
                    table.CheckConstraint("ck_inventory_unit_identity_claim_value_nonempty", "length(btrim(normalized_value)) > 0");
                    table.ForeignKey(
                        name: "fk_unit_identity_claims_units_inventory_unit_id",
                        column: x => x.inventory_unit_id,
                        principalSchema: "inventory",
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO inventory.unit_identity_claims
                    (id, inventory_unit_id, identifier_type, identifier_slot, raw_value, normalized_value, normalization_version, created_at)
                SELECT md5(id::text || ':serial')::uuid, id, 1, 1, serial_number,
                       upper(normalize(btrim(serial_number), NFKC)), 1, created_at
                FROM inventory.units
                WHERE serial_number IS NOT NULL AND length(btrim(serial_number)) > 0;

                INSERT INTO inventory.unit_identity_claims
                    (id, inventory_unit_id, identifier_type, identifier_slot, raw_value, normalized_value, normalization_version, created_at)
                SELECT md5(id::text || ':imei1')::uuid, id, 2, 2, imei1,
                       regexp_replace(normalize(btrim(imei1), NFKC), '[^0-9]', '', 'g'), 1, created_at
                FROM inventory.units
                WHERE imei1 IS NOT NULL AND length(btrim(imei1)) > 0;

                INSERT INTO inventory.unit_identity_claims
                    (id, inventory_unit_id, identifier_type, identifier_slot, raw_value, normalized_value, normalization_version, created_at)
                SELECT md5(id::text || ':imei2')::uuid, id, 2, 3, imei2,
                       regexp_replace(normalize(btrim(imei2), NFKC), '[^0-9]', '', 'g'), 1, created_at
                FROM inventory.units
                WHERE imei2 IS NOT NULL AND length(btrim(imei2)) > 0;
                """);
            migrationBuilder.CreateIndex(
                name: "ix_unit_identity_claims_identifier_type_normalized_value",
                schema: "inventory",
                table: "unit_identity_claims",
                columns: new[] { "identifier_type", "normalized_value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_unit_identity_claims_inventory_unit_id",
                schema: "inventory",
                table: "unit_identity_claims",
                column: "inventory_unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_unit_identity_claims_inventory_unit_id_identifier_slot",
                schema: "inventory",
                table: "unit_identity_claims",
                columns: new[] { "inventory_unit_id", "identifier_slot" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "unit_identity_claims",
                schema: "inventory");
        }
    }
}
