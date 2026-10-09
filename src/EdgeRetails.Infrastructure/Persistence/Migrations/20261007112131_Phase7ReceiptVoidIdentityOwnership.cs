using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase7ReceiptVoidIdentityOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM inventory.units u
                        WHERE u.serial_number IS NOT NULL AND length(btrim(u.serial_number,
                            U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000')) > 0
                          AND NOT EXISTS (
                              SELECT 1 FROM inventory.unit_identity_claims c
                              WHERE c.inventory_unit_id = u.id AND c.identifier_type = 1 AND c.identifier_slot = 1))
                       OR EXISTS (
                        SELECT 1 FROM inventory.units u
                        WHERE u.imei1 IS NOT NULL AND length(btrim(u.imei1,
                            U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000')) > 0
                          AND NOT EXISTS (
                              SELECT 1 FROM inventory.unit_identity_claims c
                              WHERE c.inventory_unit_id = u.id AND c.identifier_type = 2 AND c.identifier_slot = 2))
                       OR EXISTS (
                        SELECT 1 FROM inventory.units u
                        WHERE u.imei2 IS NOT NULL AND length(btrim(u.imei2,
                            U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000')) > 0
                          AND NOT EXISTS (
                              SELECT 1 FROM inventory.unit_identity_claims c
                              WHERE c.inventory_unit_id = u.id AND c.identifier_type = 2 AND c.identifier_slot = 3))
                    THEN
                        RAISE EXCEPTION 'ReceiptVoid ownership migration blocked: one or more populated unit identity slots lack immutable claim evidence.';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM inventory.unit_identity_claims c
                        JOIN inventory.units u ON u.id = c.inventory_unit_id
                        WHERE u.status <> 9
                        GROUP BY c.normalized_value
                        HAVING count(*) > 1)
                    THEN
                        RAISE EXCEPTION 'ReceiptVoid ownership migration blocked: active cross-slot normalized identity conflicts exist; no winner was selected.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "ix_units_imei1",
                schema: "inventory",
                table: "units");

            migrationBuilder.DropIndex(
                name: "ix_units_imei2",
                schema: "inventory",
                table: "units");

            migrationBuilder.DropIndex(
                name: "ix_units_serial_number",
                schema: "inventory",
                table: "units");

            migrationBuilder.DropIndex(
                name: "ix_unit_identity_claims_identifier_type_normalized_value",
                schema: "inventory",
                table: "unit_identity_claims");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_unit_identity_claims_id_normalized_value",
                schema: "inventory",
                table: "unit_identity_claims",
                columns: new[] { "id", "normalized_value" });

            migrationBuilder.CreateTable(
                name: "unit_identity_ownership",
                schema: "inventory",
                columns: table => new
                {
                    normalized_value = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    inventory_unit_identity_claim_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit_identity_ownership", x => x.normalized_value);
                    table.ForeignKey(
                        name: "fk_unit_identity_ownership_unit_identity_claims_inventory_unit~",
                        columns: x => new { x.inventory_unit_identity_claim_id, x.normalized_value },
                        principalSchema: "inventory",
                        principalTable: "unit_identity_claims",
                        principalColumns: new[] { "id", "normalized_value" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO inventory.unit_identity_ownership
                    (normalized_value, inventory_unit_identity_claim_id)
                SELECT c.normalized_value, c.id
                FROM inventory.unit_identity_claims c
                JOIN inventory.units u ON u.id = c.inventory_unit_id
                WHERE u.status <> 9;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_units_imei1",
                schema: "inventory",
                table: "units",
                column: "imei1",
                filter: "imei1 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_units_imei2",
                schema: "inventory",
                table: "units",
                column: "imei2",
                filter: "imei2 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_units_serial_number",
                schema: "inventory",
                table: "units",
                column: "serial_number",
                filter: "serial_number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_unit_identity_claims_identifier_type_normalized_value",
                schema: "inventory",
                table: "unit_identity_claims",
                columns: new[] { "identifier_type", "normalized_value" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_identity_ownership_inventory_unit_identity_claim_id",
                schema: "inventory",
                table: "unit_identity_ownership",
                column: "inventory_unit_identity_claim_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_unit_identity_ownership_inventory_unit_identity_claim_id_no~",
                schema: "inventory",
                table: "unit_identity_ownership",
                columns: new[] { "inventory_unit_identity_claim_id", "normalized_value" });

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION inventory.has_governed_receipt_void(p_unit_id uuid)
                RETURNS boolean LANGUAGE sql STABLE AS $receipt_void_proof$
                SELECT EXISTS (
                    SELECT 1
                    FROM inventory.units u
                    JOIN purchasing.purchase_item_units piu ON piu.inventory_unit_id = u.id
                    JOIN purchasing.purchase_items pi ON pi.id = piu.purchase_item_id
                    JOIN purchasing.purchases p ON p.id = pi.purchase_id
                    JOIN purchasing.purchase_voids pv ON pv.purchase_id = p.id
                    JOIN inventory.movement_units mu
                      ON mu.inventory_unit_id = u.id
                     AND mu.from_status = 1
                     AND mu.to_status = 9
                    JOIN inventory.movements m
                      ON m.id = mu.movement_id
                     AND m.movement_type = 18
                     AND m.reference_type = 'PURCHASE_VOID'
                     AND m.reference_id = p.id
                     AND m.correlation_id = pv.client_operation_id
                    WHERE u.id = p_unit_id
                      AND u.status = 9
                      AND p.status = 2
                ) $receipt_void_proof$;

                CREATE OR REPLACE FUNCTION inventory.guard_unit_identity_ownership()
                RETURNS trigger LANGUAGE plpgsql AS $ownership$
                DECLARE
                    claim_unit_id uuid;
                    unit_status integer;
                    claim_identifier_type integer;
                    claim_identifier_slot integer;
                    claim_normalized_value text;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        SELECT c.inventory_unit_id, u.status, c.identifier_type,
                               c.identifier_slot, c.normalized_value
                        INTO claim_unit_id, unit_status, claim_identifier_type,
                             claim_identifier_slot, claim_normalized_value
                        FROM inventory.unit_identity_claims c
                        JOIN inventory.units u ON u.id = c.inventory_unit_id
                        WHERE c.id = NEW.inventory_unit_identity_claim_id
                          AND c.normalized_value = NEW.normalized_value;
                        IF NOT FOUND THEN
                            RAISE EXCEPTION 'Identity ownership must reference matching immutable claim evidence.';
                        END IF;
                        IF unit_status = 9 THEN
                            RAISE EXCEPTION 'ReceiptVoided historical units cannot acquire active identity ownership.';
                        END IF;
                        IF NOT (
                            (claim_identifier_type = 1 AND claim_identifier_slot = 1 AND EXISTS (
                                SELECT 1 FROM inventory.units u
                                WHERE u.id = claim_unit_id AND u.serial_number = claim_normalized_value))
                            OR (claim_identifier_type = 2 AND claim_identifier_slot = 2 AND EXISTS (
                                SELECT 1 FROM inventory.units u
                                WHERE u.id = claim_unit_id AND u.imei1 = claim_normalized_value))
                            OR (claim_identifier_type = 2 AND claim_identifier_slot = 3 AND EXISTS (
                                SELECT 1 FROM inventory.units u
                                WHERE u.id = claim_unit_id AND u.imei2 = claim_normalized_value)))
                        THEN
                            RAISE EXCEPTION 'Identity ownership must match the unit manufacturer-identity slot.';
                        END IF;
                        RETURN NEW;
                    ELSIF TG_OP = 'DELETE' THEN
                        SELECT c.inventory_unit_id, u.status INTO claim_unit_id, unit_status
                        FROM inventory.unit_identity_claims c
                        JOIN inventory.units u ON u.id = c.inventory_unit_id
                        WHERE c.id = OLD.inventory_unit_identity_claim_id;
                        IF FOUND AND (unit_status <> 9 OR NOT inventory.has_governed_receipt_void(claim_unit_id)) THEN
                            RAISE EXCEPTION 'Active manufacturer identity ownership can be released only by a persisted governed ReceiptVoid.';
                        END IF;
                        RETURN OLD;
                    END IF;
                    RAISE EXCEPTION 'Identity ownership rows cannot be updated.';
                END $ownership$;

                CREATE TRIGGER trg_unit_identity_ownership_insert_guard
                BEFORE INSERT OR UPDATE ON inventory.unit_identity_ownership
                FOR EACH ROW EXECUTE FUNCTION inventory.guard_unit_identity_ownership();

                CREATE CONSTRAINT TRIGGER trg_unit_identity_ownership_delete_guard
                AFTER DELETE ON inventory.unit_identity_ownership
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION inventory.guard_unit_identity_ownership();

                CREATE OR REPLACE FUNCTION inventory.reject_unit_identity_claim_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $claim_immutable$
                BEGIN
                    RAISE EXCEPTION 'Manufacturer identity claims are immutable historical evidence.';
                END $claim_immutable$;

                CREATE TRIGGER trg_unit_identity_claims_immutable
                BEFORE UPDATE OR DELETE ON inventory.unit_identity_claims
                FOR EACH ROW EXECUTE FUNCTION inventory.reject_unit_identity_claim_mutation();

                CREATE OR REPLACE FUNCTION inventory.guard_unit_identity_claim_insert()
                RETURNS trigger LANGUAGE plpgsql AS $claim_insert$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM inventory.units u
                        WHERE u.id = NEW.inventory_unit_id
                          AND u.status <> 9
                          AND ((NEW.identifier_type = 1 AND NEW.identifier_slot = 1
                                AND u.serial_number = NEW.normalized_value)
                            OR (NEW.identifier_type = 2 AND NEW.identifier_slot = 2
                                AND u.imei1 = NEW.normalized_value)
                            OR (NEW.identifier_type = 2 AND NEW.identifier_slot = 3
                                AND u.imei2 = NEW.normalized_value)))
                    THEN
                        RAISE EXCEPTION 'Identity claim insertion must match a populated, non-ReceiptVoided unit slot.';
                    END IF;
                    RETURN NEW;
                END $claim_insert$;

                CREATE TRIGGER trg_unit_identity_claim_insert_guard
                BEFORE INSERT ON inventory.unit_identity_claims
                FOR EACH ROW EXECUTE FUNCTION inventory.guard_unit_identity_claim_insert();

                CREATE OR REPLACE FUNCTION inventory.guard_unit_identity_claim_owner()
                RETURNS trigger LANGUAGE plpgsql AS $claim_owner$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM inventory.unit_identity_ownership o
                        WHERE o.inventory_unit_identity_claim_id = NEW.id
                          AND o.normalized_value = NEW.normalized_value)
                    THEN
                        RAISE EXCEPTION 'Every new manufacturer identity claim must acquire its matching active owner in the same transaction.';
                    END IF;
                    RETURN NEW;
                END $claim_owner$;

                CREATE CONSTRAINT TRIGGER trg_unit_identity_claim_owner
                AFTER INSERT ON inventory.unit_identity_claims
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION inventory.guard_unit_identity_claim_owner();

                CREATE OR REPLACE FUNCTION inventory.guard_unit_identity_claim_slots()
                RETURNS trigger LANGUAGE plpgsql AS $claim_slots$
                DECLARE
                    claim_count integer;
                    identity_changed boolean;
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        identity_changed := NEW.serial_number IS DISTINCT FROM OLD.serial_number OR
                                             NEW.imei1 IS DISTINCT FROM OLD.imei1 OR
                                             NEW.imei2 IS DISTINCT FROM OLD.imei2;
                        IF identity_changed AND (
                            coalesce(btrim(OLD.serial_number), '') <> '' OR
                            coalesce(btrim(OLD.imei1), '') <> '' OR
                            coalesce(btrim(OLD.imei2), '') <> '')
                        THEN
                            RAISE EXCEPTION 'Manufacturer identity on an inventory unit is immutable after first assignment.';
                        END IF;
                        IF NEW.status = 9 AND EXISTS (
                            SELECT 1 FROM inventory.unit_identity_claims c
                            JOIN inventory.unit_identity_ownership o
                              ON o.inventory_unit_identity_claim_id = c.id
                            WHERE c.inventory_unit_id = NEW.id)
                        THEN
                            RAISE EXCEPTION 'ReceiptVoided history cannot retain active identity ownership.';
                        END IF;
                        IF NOT identity_changed THEN
                            RETURN NEW;
                        END IF;
                    END IF;
                    IF NEW.status = 9 THEN
                        RAISE EXCEPTION 'New inventory units cannot be created as ReceiptVoided history.';
                    END IF;
                    IF NEW.serial_number IS NOT NULL AND btrim(NEW.serial_number) <> '' THEN
                        SELECT count(*) INTO claim_count
                        FROM inventory.unit_identity_claims c
                        JOIN inventory.unit_identity_ownership o
                          ON o.inventory_unit_identity_claim_id = c.id
                         AND o.normalized_value = c.normalized_value
                        WHERE c.inventory_unit_id = NEW.id AND c.identifier_type = 1
                          AND c.identifier_slot = 1 AND c.normalized_value = NEW.serial_number;
                        IF claim_count <> 1 THEN
                            RAISE EXCEPTION 'Inventory unit Serial requires exactly one matching claim and active owner.';
                        END IF;
                    END IF;
                    IF NEW.imei1 IS NOT NULL AND btrim(NEW.imei1) <> '' THEN
                        SELECT count(*) INTO claim_count
                        FROM inventory.unit_identity_claims c
                        JOIN inventory.unit_identity_ownership o
                          ON o.inventory_unit_identity_claim_id = c.id
                         AND o.normalized_value = c.normalized_value
                        WHERE c.inventory_unit_id = NEW.id AND c.identifier_type = 2
                          AND c.identifier_slot = 2 AND c.normalized_value = NEW.imei1;
                        IF claim_count <> 1 THEN
                            RAISE EXCEPTION 'Inventory unit IMEI1 requires exactly one matching claim and active owner.';
                        END IF;
                    END IF;
                    IF NEW.imei2 IS NOT NULL AND btrim(NEW.imei2) <> '' THEN
                        SELECT count(*) INTO claim_count
                        FROM inventory.unit_identity_claims c
                        JOIN inventory.unit_identity_ownership o
                          ON o.inventory_unit_identity_claim_id = c.id
                         AND o.normalized_value = c.normalized_value
                        WHERE c.inventory_unit_id = NEW.id AND c.identifier_type = 2
                          AND c.identifier_slot = 3 AND c.normalized_value = NEW.imei2;
                        IF claim_count <> 1 THEN
                            RAISE EXCEPTION 'Inventory unit IMEI2 requires exactly one matching claim and active owner.';
                        END IF;
                    END IF;
                    RETURN NEW;
                END $claim_slots$;

                CREATE CONSTRAINT TRIGGER trg_unit_identity_claim_slots
                AFTER INSERT OR UPDATE ON inventory.units
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION inventory.guard_unit_identity_claim_slots();

                CREATE OR REPLACE FUNCTION inventory.guard_receipt_void_identity_ownership()
                RETURNS trigger LANGUAGE plpgsql AS $unit_ownership$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        IF OLD.status = 9 THEN
                            RAISE EXCEPTION 'ReceiptVoided inventory units are immutable terminal history.';
                        END IF;
                        RETURN OLD;
                    END IF;
                    IF OLD.status = 9 AND NEW IS DISTINCT FROM OLD THEN
                        RAISE EXCEPTION 'ReceiptVoided inventory units are immutable terminal history.';
                    END IF;
                    IF OLD.status IS DISTINCT FROM NEW.status AND NEW.status = 9 THEN
                        IF OLD.status <> 1 OR NOT inventory.has_governed_receipt_void(NEW.id) THEN
                            RAISE EXCEPTION 'ReceiptVoid status requires persisted PurchaseVoid provenance from InStock.';
                        END IF;
                    END IF;
                    IF NEW.status = 9 AND EXISTS (
                        SELECT 1 FROM inventory.unit_identity_claims c
                        JOIN inventory.unit_identity_ownership o
                          ON o.inventory_unit_identity_claim_id = c.id
                         AND o.normalized_value = c.normalized_value
                        WHERE c.inventory_unit_id = NEW.id)
                    THEN
                        RAISE EXCEPTION 'ReceiptVoid must release active manufacturer identity ownership in the same transaction.';
                    END IF;
                    RETURN NEW;
                END $unit_ownership$;

                CREATE TRIGGER trg_receipt_void_identity_ownership_delete_guard
                BEFORE DELETE ON inventory.units
                FOR EACH ROW EXECUTE FUNCTION inventory.guard_receipt_void_identity_ownership();

                CREATE CONSTRAINT TRIGGER trg_receipt_void_identity_ownership_guard
                AFTER UPDATE ON inventory.units
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION inventory.guard_receipt_void_identity_ownership();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    RAISE EXCEPTION 'Phase7ReceiptVoidIdentityOwnership is forward-only; roll forward with a corrective migration.';
                END $$;
                """);
        }
    }
}
