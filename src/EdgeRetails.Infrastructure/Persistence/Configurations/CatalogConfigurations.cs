using EdgeRetails.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> builder)
    {
        builder.ToTable(
            "units",
            "catalog",
            table => table.HasCheckConstraint(
                "ck_units_display_decimal_places",
                "display_decimal_places BETWEEN 0 AND 6"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Symbol).HasMaxLength(20).IsRequired();
        builder.Property(x => x.DisplayDecimalPlaces).HasDefaultValue(0);

        builder.HasIndex(x => x.Name).IsUnique();
        builder.HasIndex(x => x.Symbol).IsUnique();
    }
}

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories", "catalog");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(150).IsRequired();
        builder.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable(
            "products",
            "catalog",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_products_nonnegative_prices",
                    "default_sale_price >= 0 AND minimum_stock_level >= 0 AND (reference_purchase_cost IS NULL OR reference_purchase_cost >= 0)");
                table.HasCheckConstraint(
                    "ck_products_warranty_months_nonnegative",
                    "default_warranty_months >= 0");
                table.HasCheckConstraint(
                    "ck_products_attributes_schema_version_positive",
                    "attributes_schema_version >= 1");
            });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(250).IsRequired();
        builder.Property(x => x.Sku).HasMaxLength(100);
        builder.Property(x => x.Brand).HasMaxLength(150);
        builder.Property(x => x.Model).HasMaxLength(150);
        builder.Property(x => x.ReferencePurchaseCost).HasPrecision(18, 6);
        builder.Property(x => x.DefaultSalePrice).HasPrecision(18, 2);
        builder.Property(x => x.MinimumStockLevel).HasPrecision(18, 6).HasDefaultValue(0m);
        builder.Property(x => x.DefaultWarrantyMonths).HasDefaultValue(0);
        builder.Property(x => x.AttributesJson).HasMaxLength(8000);
        builder.Property(x => x.AttributesSchemaVersion).HasDefaultValue(1);
        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasOne<Unit>()
            .WithMany()
            .HasForeignKey(x => x.BaseUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.Sku)
            .IsUnique()
            .HasFilter("sku IS NOT NULL");

        builder.HasIndex(x => x.Name);
        builder.HasIndex(x => new { x.CategoryId, x.IsActive });
    }
}

internal sealed class ProductUnitConfiguration : IEntityTypeConfiguration<ProductUnit>
{
    public void Configure(EntityTypeBuilder<ProductUnit> builder)
    {
        builder.ToTable(
            "product_units",
            "catalog",
            table => table.HasCheckConstraint(
                "ck_product_units_factor_positive",
                "factor_to_base_unit > 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.FactorToBaseUnit).HasPrecision(18, 9);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Unit>()
            .WithMany()
            .HasForeignKey(x => x.UnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.ProductId, x.UnitId }).IsUnique();

        // Default-purchase/default-sale partial unique indexes are created
        // as explicit PostgreSQL migration SQL because EF cannot model two
        // indexes over the same key with different filters independently.
    }
}

internal sealed class ProductUnitBarcodeConfiguration
    : IEntityTypeConfiguration<ProductUnitBarcode>
{
    public void Configure(EntityTypeBuilder<ProductUnitBarcode> builder)
    {
        builder.ToTable("product_unit_barcodes", "catalog");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Barcode).HasMaxLength(150).IsRequired();

        builder.HasOne<ProductUnit>()
            .WithMany()
            .HasForeignKey(x => x.ProductUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.Barcode)
            .IsUnique()
            .HasFilter("is_active = TRUE");

        builder.HasIndex(x => x.ProductUnitId);
    }
}
