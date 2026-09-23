START TRANSACTION;
ALTER TABLE catalog.products ADD attributes_json character varying(8000);

ALTER TABLE catalog.products ADD attributes_schema_version integer NOT NULL DEFAULT 1;

ALTER TABLE catalog.products ADD CONSTRAINT ck_products_attributes_schema_version_positive CHECK (attributes_schema_version >= 1);

ALTER TABLE inventory.units ADD source_stock_adjustment_item_id uuid;

CREATE INDEX ix_units_source_stock_adjustment_item_id ON inventory.units (source_stock_adjustment_item_id);

INSERT INTO system.__ef_migrations_history ("MigrationId", "ProductVersion")
VALUES ('20260922120000_Phase1CanonicalSchemaAlignment', '10.0.12');

COMMIT;

