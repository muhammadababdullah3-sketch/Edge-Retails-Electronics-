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
                -- .NET Trim whitespace is explicit here. For ASCII, NFKC is the
                -- identity and invariant casing is locale-independent translation.
                -- Non-ASCII legacy values need an explicitly reviewed runtime
                -- cutover; never assume PostgreSQL upper/NFKC equals .NET Unicode.
                CREATE OR REPLACE FUNCTION pg_temp.tracking_serial_v1(raw text)
                RETURNS text LANGUAGE plpgsql AS $serial$
                DECLARE value text;
                BEGIN
                    value := btrim(raw, U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000');
                    IF value IS NULL OR length(value) = 0 THEN RETURN NULL; END IF;
                    IF EXISTS (SELECT 1 FROM regexp_split_to_table(value, '') c WHERE ascii(c) < 32 OR ascii(c) = 127) THEN
                        RAISE EXCEPTION 'Tracking manufacturer identity cutover blocked: invalid control character in legacy Serial.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM regexp_split_to_table(value, '') c WHERE ascii(c) > 126) THEN
                        RAISE EXCEPTION 'Tracking manufacturer identity cutover blocked: non-ASCII/invisible legacy Serial requires explicit runtime-normalized cutover review.';
                    END IF;
                    IF length(value) > 160 THEN
                        RAISE EXCEPTION 'Tracking manufacturer identity cutover blocked: legacy Serial exceeds canonical length.';
                    END IF;
                    RETURN translate(value, 'abcdefghijklmnopqrstuvwxyz', 'ABCDEFGHIJKLMNOPQRSTUVWXYZ');
                END $serial$;

                DO $$
                BEGIN
                    -- Evaluate every value before collision/backfill, including
                    -- whitespace-only optional fields and invisible-only values.
                    PERFORM pg_temp.tracking_serial_v1(serial_number) FROM inventory.units;
                    IF EXISTS (
                        SELECT 1
                        FROM inventory.units
                        WHERE (imei1 IS NOT NULL AND length(btrim(imei1)) > 0 AND
                               (normalize(btrim(imei1), NFKC) !~ '^[0-9[:space:]-]+$' OR
                                length(regexp_replace(normalize(btrim(imei1), NFKC), '[^0-9]', '', 'g')) NOT IN (14, 15)))
                           OR (imei2 IS NOT NULL AND length(btrim(imei2)) > 0 AND
                               (normalize(btrim(imei2), NFKC) !~ '^[0-9[:space:]-]+$' OR
                                length(regexp_replace(normalize(btrim(imei2), NFKC), '[^0-9]', '', 'g')) NOT IN (14, 15)))
                    ) THEN
                        RAISE EXCEPTION 'Tracking manufacturer identity cutover blocked: invalid legacy IMEI format.';
                    END IF;

                    IF EXISTS (
                        SELECT normalized_serial
                        FROM (
                            SELECT pg_temp.tracking_serial_v1(serial_number) AS normalized_serial,
                                   count(DISTINCT id) AS units
                            FROM inventory.units
                            WHERE pg_temp.tracking_serial_v1(serial_number) IS NOT NULL
                            GROUP BY pg_temp.tracking_serial_v1(serial_number)
                        ) s
                        WHERE units > 1
                    ) THEN
                        RAISE EXCEPTION 'Tracking manufacturer identity cutover blocked: normalized Serial collision.';
                    END IF;

                    IF EXISTS (
                        SELECT normalized_imei
                        FROM (
                            SELECT regexp_replace(normalize(btrim(value), NFKC), '[^0-9]', '', 'g') AS normalized_imei,
                                   count(DISTINCT unit_id) AS units
                            FROM (
                                SELECT id AS unit_id, imei1 AS value FROM inventory.units WHERE imei1 IS NOT NULL AND length(btrim(imei1)) > 0
                                UNION ALL
                                SELECT id AS unit_id, imei2 AS value FROM inventory.units WHERE imei2 IS NOT NULL AND length(btrim(imei2)) > 0
                            ) i
                            GROUP BY regexp_replace(normalize(btrim(value), NFKC), '[^0-9]', '', 'g')
                        ) x
                        WHERE units > 1
                    ) THEN
                        RAISE EXCEPTION 'Tracking manufacturer identity cutover blocked: cross-slot IMEI collision.';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM inventory.units
                        WHERE imei1 IS NOT NULL AND imei2 IS NOT NULL
                          AND length(btrim(imei1)) > 0 AND length(btrim(imei2)) > 0
                          AND regexp_replace(normalize(btrim(imei1), NFKC), '[^0-9]', '', 'g') =
                              regexp_replace(normalize(btrim(imei2), NFKC), '[^0-9]', '', 'g')
                    ) THEN
                        RAISE EXCEPTION 'Tracking manufacturer identity cutover blocked: duplicate IMEI slots on one unit.';
                    END IF;
                END $$;
                """);
            migrationBuilder.Sql(
                """
                INSERT INTO inventory.unit_identity_claims
                    (id, inventory_unit_id, identifier_type, identifier_slot, raw_value, normalized_value, normalization_version, created_at)
                SELECT md5(id::text || ':serial')::uuid, id, 1, 1, serial_number,
                       pg_temp.tracking_serial_v1(serial_number), 1, created_at
                FROM inventory.units
                WHERE pg_temp.tracking_serial_v1(serial_number) IS NOT NULL;

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
