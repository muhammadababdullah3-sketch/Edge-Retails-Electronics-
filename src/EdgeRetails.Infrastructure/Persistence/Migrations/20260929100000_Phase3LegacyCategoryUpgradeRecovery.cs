using System;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations;

[DbContext(typeof(EdgeRetailsDbContext))]
[Migration("20260929100000_Phase3LegacyCategoryUpgradeRecovery")]
public sealed class Phase3LegacyCategoryUpgradeRecovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DO $phase3$
            DECLARE
                live_count bigint;
                held_count bigint;
                overflow_count bigint;
                mismatch_count bigint;
                invalid_count bigint;
            BEGIN
                SELECT count(*) INTO live_count FROM catalog.categories;

                IF to_regclass('system.phase3_legacy_category_hold') IS NOT NULL THEN
                    IF live_count <> 0 THEN
                        RAISE EXCEPTION 'Legacy category hold exists, but catalog.categories is not empty';
                    END IF;

                    EXECUTE 'SELECT count(*) FROM system.phase3_legacy_category_hold'
                        INTO held_count;
                    IF held_count = 0 THEN
                        RAISE EXCEPTION 'Legacy category hold is empty';
                    END IF;

                    EXECUTE $overflow$
                        WITH source AS (
                            SELECT id, name,
                                   coalesce(
                                       translate(
                                           substring(name COLLATE "C" FROM '[A-Za-z]'),
                                           'abcdefghijklmnopqrstuvwxyz',
                                           'ABCDEFGHIJKLMNOPQRSTUVWXYZ'),
                                       'C') AS prefix
                            FROM system.phase3_legacy_category_hold
                        ), ranked AS (
                            SELECT row_number() OVER (
                                       PARTITION BY prefix
                                       ORDER BY name COLLATE "C", id) - 1 AS ordinal
                            FROM source
                        )
                        SELECT count(*) FROM ranked WHERE ordinal >= 46656
                    $overflow$ INTO overflow_count;
                    IF overflow_count <> 0 THEN
                        RAISE EXCEPTION 'Legacy category symbol space exhausted for one prefix';
                    END IF;

                    EXECUTE $restore$
                        WITH source AS (
                            SELECT id, name, is_active,
                                   coalesce(
                                       translate(
                                           substring(name COLLATE "C" FROM '[A-Za-z]'),
                                           'abcdefghijklmnopqrstuvwxyz',
                                           'ABCDEFGHIJKLMNOPQRSTUVWXYZ'),
                                       'C') AS prefix
                            FROM system.phase3_legacy_category_hold
                        ), ranked AS (
                            SELECT id, name, is_active, prefix,
                                   row_number() OVER (
                                       PARTITION BY prefix
                                       ORDER BY name COLLATE "C", id) - 1 AS ordinal
                            FROM source
                        )
                        INSERT INTO catalog.categories
                            (id, name, identity_symbol, is_active, version)
                        SELECT id, name,
                               prefix ||
                               substr('0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ',
                                      ((ordinal / 1296) % 36)::integer + 1, 1) ||
                               substr('0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ',
                                      ((ordinal / 36) % 36)::integer + 1, 1) ||
                               substr('0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ',
                                      (ordinal % 36)::integer + 1, 1),
                               is_active, 0
                        FROM ranked
                        ORDER BY prefix, ordinal
                    $restore$;

                    EXECUTE $compare$
                        SELECT count(*) FROM (
                            (SELECT id, name, is_active
                             FROM system.phase3_legacy_category_hold
                             EXCEPT
                             SELECT id, name, is_active FROM catalog.categories)
                            UNION ALL
                            (SELECT id, name, is_active FROM catalog.categories
                             EXCEPT
                             SELECT id, name, is_active
                             FROM system.phase3_legacy_category_hold)
                        ) AS differences
                    $compare$ INTO mismatch_count;

                    SELECT count(*) INTO invalid_count
                    FROM catalog.categories
                    WHERE identity_symbol !~ '^[A-Z][A-Z0-9]{3}$'
                       OR version <> 0;

                    IF mismatch_count <> 0 OR invalid_count <> 0 OR
                       (SELECT count(*) FROM catalog.categories) <> held_count OR
                       (SELECT count(DISTINCT identity_symbol)
                        FROM catalog.categories) <> held_count THEN
                        RAISE EXCEPTION 'Legacy category restore validation failed';
                    END IF;

                    EXECUTE 'DROP TABLE system.phase3_legacy_category_hold';
                ELSIF live_count = 1 THEN
                    UPDATE catalog.categories
                    SET identity_symbol =
                        coalesce(
                            translate(
                                substring(name COLLATE "C" FROM '[A-Za-z]'),
                                'abcdefghijklmnopqrstuvwxyz',
                                'ABCDEFGHIJKLMNOPQRSTUVWXYZ'),
                            'C') || '000'
                    WHERE identity_symbol = '';

                    IF EXISTS (
                        SELECT 1 FROM catalog.categories
                        WHERE identity_symbol !~ '^[A-Z][A-Z0-9]{0,3}$'
                    ) THEN
                        RAISE EXCEPTION 'Single legacy category has an invalid identity symbol';
                    END IF;
                ELSIF live_count > 1 THEN
                    RAISE EXCEPTION 'Unexpected multiple categories without a legacy hold';
                END IF;

                ALTER TABLE catalog.categories
                    ALTER COLUMN identity_symbol DROP DEFAULT;
            END
            $phase3$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException(
            "Phase 3 legacy category recovery is forward-only.");
    }
}
