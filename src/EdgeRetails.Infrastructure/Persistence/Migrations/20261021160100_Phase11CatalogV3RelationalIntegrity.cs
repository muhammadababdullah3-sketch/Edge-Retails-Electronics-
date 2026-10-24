using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase11CatalogV3RelationalIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The taxonomy is reference data managed by the importer, not operational EF entities.
            // This migration owns the catalog_v3 schema and guarantees its relational constraints.
            // CREATE IF NOT EXISTS supports installations where the prior importer created the same
            // table shapes before this migration; incompatible existing objects fail at constraint checks.
            migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS catalog_v3;");
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS catalog_v3.divisions (
                    code TEXT PRIMARY KEY,
                    name TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS catalog_v3.departments (
                    code TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    division_code TEXT NOT NULL,
                    active_leaf_count INT NOT NULL DEFAULT 0,
                    deprecated_count INT NOT NULL DEFAULT 0,
                    navigation_only_count INT NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS catalog_v3.product_classes (
                    class_ref TEXT PRIMARY KEY,
                    class_name TEXT NOT NULL,
                    division_code TEXT NOT NULL,
                    department_code TEXT NOT NULL,
                    class_type TEXT NOT NULL,
                    class_status TEXT NOT NULL,
                    catalog_role TEXT NOT NULL,
                    canonical_class_ref TEXT,
                    classification_rule TEXT
                );
                CREATE TABLE IF NOT EXISTS catalog_v3.class_redirects (
                    legacy_class_ref TEXT PRIMARY KEY,
                    legacy_label TEXT NOT NULL,
                    canonical_class_ref TEXT NOT NULL,
                    canonical_label TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    migration_policy TEXT NOT NULL,
                    status TEXT NOT NULL
                );
                """);

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    delete_kind "char";
                    product_class_ref smallint;
                    department_division_code smallint;
                    product_class_division_code smallint;
                    product_class_department_code smallint;
                    redirect_canonical_class_ref smallint;
                BEGIN
                    SELECT attnum::smallint INTO department_division_code
                    FROM pg_attribute WHERE attrelid='catalog_v3.departments'::regclass AND attname='division_code' AND NOT attisdropped;
                    SELECT attnum::smallint INTO product_class_division_code
                    FROM pg_attribute WHERE attrelid='catalog_v3.product_classes'::regclass AND attname='division_code' AND NOT attisdropped;
                    SELECT attnum::smallint INTO product_class_department_code
                    FROM pg_attribute WHERE attrelid='catalog_v3.product_classes'::regclass AND attname='department_code' AND NOT attisdropped;
                    SELECT attnum::smallint INTO redirect_canonical_class_ref
                    FROM pg_attribute WHERE attrelid='catalog_v3.class_redirects'::regclass AND attname='canonical_class_ref' AND NOT attisdropped;

                    SELECT confdeltype INTO delete_kind FROM pg_constraint
                    WHERE conrelid='catalog_v3.departments'::regclass AND contype='f'
                      AND confrelid='catalog_v3.divisions'::regclass AND conkey=ARRAY[department_division_code]::smallint[] LIMIT 1;
                    IF delete_kind IS NULL THEN
                        ALTER TABLE catalog_v3.departments ADD CONSTRAINT fk_catalog_v3_departments_divisions_division_code
                            FOREIGN KEY (division_code) REFERENCES catalog_v3.divisions(code) ON DELETE RESTRICT;
                    ELSIF delete_kind <> 'r' THEN
                        RAISE EXCEPTION 'CATALOG_V3_CONSTRAINT_CONFLICT: departments.division_code FK must be RESTRICT';
                    END IF;

                    SELECT confdeltype INTO delete_kind FROM pg_constraint
                    WHERE conrelid='catalog_v3.product_classes'::regclass AND contype='f'
                      AND confrelid='catalog_v3.divisions'::regclass AND conkey=ARRAY[product_class_division_code]::smallint[] LIMIT 1;
                    IF delete_kind IS NULL THEN
                        ALTER TABLE catalog_v3.product_classes ADD CONSTRAINT fk_catalog_v3_product_classes_divisions_division_code
                            FOREIGN KEY (division_code) REFERENCES catalog_v3.divisions(code) ON DELETE RESTRICT;
                    ELSIF delete_kind <> 'r' THEN
                        RAISE EXCEPTION 'CATALOG_V3_CONSTRAINT_CONFLICT: product_classes.division_code FK must be RESTRICT';
                    END IF;

                    SELECT confdeltype INTO delete_kind FROM pg_constraint
                    WHERE conrelid='catalog_v3.product_classes'::regclass AND contype='f'
                      AND confrelid='catalog_v3.departments'::regclass AND conkey=ARRAY[product_class_department_code]::smallint[] LIMIT 1;
                    IF delete_kind IS NULL THEN
                        ALTER TABLE catalog_v3.product_classes ADD CONSTRAINT fk_catalog_v3_product_classes_departments_department_code
                            FOREIGN KEY (department_code) REFERENCES catalog_v3.departments(code) ON DELETE RESTRICT;
                    ELSIF delete_kind <> 'r' THEN
                        RAISE EXCEPTION 'CATALOG_V3_CONSTRAINT_CONFLICT: product_classes.department_code FK must be RESTRICT';
                    END IF;

                    SELECT confdeltype INTO delete_kind FROM pg_constraint
                    WHERE conrelid='catalog_v3.class_redirects'::regclass AND contype='f'
                      AND confrelid='catalog_v3.product_classes'::regclass AND conkey=ARRAY[redirect_canonical_class_ref]::smallint[] LIMIT 1;
                    IF delete_kind IS NULL THEN
                        ALTER TABLE catalog_v3.class_redirects ADD CONSTRAINT fk_catalog_v3_class_redirects_product_classes_canonical_class_ref
                            FOREIGN KEY (canonical_class_ref) REFERENCES catalog_v3.product_classes(class_ref) ON DELETE RESTRICT;
                    ELSIF delete_kind <> 'r' THEN
                        RAISE EXCEPTION 'CATALOG_V3_CONSTRAINT_CONFLICT: class_redirects.canonical_class_ref FK must be RESTRICT';
                    END IF;
                END $$;
                """);

            // Product.ClassRef was introduced by 20261021120000_Phase11CatalogCanonicalIntegration.
            // Fail closed if that predecessor migration/model was not applied; never add columns or
            // constraints ad hoc from the seed/importer path.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    product_class_ref smallint;
                    delete_kind "char";
                BEGIN
                    SELECT attnum::smallint INTO product_class_ref
                    FROM pg_attribute WHERE attrelid='catalog.products'::regclass AND attname='class_ref' AND NOT attisdropped;
                    IF product_class_ref IS NULL THEN
                        RAISE EXCEPTION 'CATALOG_V3_MIGRATION_PREREQUISITE: apply Phase11CatalogCanonicalIntegration first';
                    END IF;

                    SELECT confdeltype INTO delete_kind FROM pg_constraint
                    WHERE conrelid='catalog.products'::regclass AND contype='f'
                      AND confrelid='catalog_v3.product_classes'::regclass AND conkey=ARRAY[product_class_ref]::smallint[] LIMIT 1;
                    IF delete_kind IS NULL THEN
                        ALTER TABLE catalog.products ADD CONSTRAINT fk_products_class_ref_catalog_v3_product_classes_class_ref
                            FOREIGN KEY (class_ref) REFERENCES catalog_v3.product_classes(class_ref) ON DELETE RESTRICT;
                    ELSIF delete_kind <> 'r' THEN
                        RAISE EXCEPTION 'CATALOG_V3_CONSTRAINT_CONFLICT: products.class_ref FK must be RESTRICT';
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_catalog_v3_departments_division_code ON catalog_v3.departments (division_code);");
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_catalog_v3_product_classes_department_code ON catalog_v3.product_classes (department_code);");
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_catalog_v3_product_classes_division_code ON catalog_v3.product_classes (division_code);");
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_catalog_v3_class_redirects_canonical_class_ref ON catalog_v3.class_redirects (canonical_class_ref);");
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_products_class_ref ON catalog.products (class_ref);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Preserve taxonomy reference records/schema on rollback. Remove only the operational
            // FK/index, and only when no product has a ClassRef. A later migration/bootstrap may
            // reuse the retained reference data without losing source identity.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM catalog.products WHERE class_ref IS NOT NULL) THEN
                        RAISE EXCEPTION 'CATALOG_V3_ROLLBACK_BLOCKED: product class references exist';
                    END IF;
                END $$;
                """);
            migrationBuilder.Sql("ALTER TABLE catalog.products DROP CONSTRAINT IF EXISTS fk_products_class_ref_catalog_v3_product_classes_class_ref;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS catalog.ix_products_class_ref;");
        }
    }
}

