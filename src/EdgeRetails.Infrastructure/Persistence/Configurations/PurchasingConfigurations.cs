using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class PurchaseConfiguration : IEntityTypeConfiguration<Purchase>
{
    public void Configure(EntityTypeBuilder<Purchase> builder)
    {
        builder.ToTable(
            "purchases",
            "purchasing",
            table => table.HasCheckConstraint(
                "ck_purchase_totals",
                "subtotal >= 0 AND other_charges >= 0 AND grand_total >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.PurchaseNumber).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SupplierInvoiceNumber).HasMaxLength(120).IsRequired();
        builder.Property(x => x.NormalizedSupplierInvoiceNumber).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Note).HasMaxLength(2000);
        builder.Property(x => x.Subtotal).HasPrecision(18, 2);
        builder.Property(x => x.OtherCharges).HasPrecision(18, 2);
        builder.Property(x => x.GrandTotal).HasPrecision(18, 2);
        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(x => x.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.PurchaseNumber).IsUnique();
        builder.HasIndex(x => x.ClientOperationId).IsUnique();
        builder.HasIndex(x => new { x.SupplierId, x.NormalizedSupplierInvoiceNumber }).IsUnique();
        builder.HasIndex(x => new { x.SupplierId, x.PurchaseDate });
        builder.HasIndex(x => new { x.PurchaseDate, x.Id })
            .IsDescending(true, true)
            .HasDatabaseName("ix_purchases_purchase_date_id");
    }
}

internal sealed class PurchaseItemConfiguration : IEntityTypeConfiguration<PurchaseItem>
{
    public void Configure(EntityTypeBuilder<PurchaseItem> builder)
    {
        builder.ToTable(
            "purchase_items",
            "purchasing",
            table => table.HasCheckConstraint(
                "ck_purchase_item_values",
                "entered_quantity > 0 AND factor_to_base_snapshot > 0 AND base_quantity > 0 AND entered_unit_cost >= 0 AND base_line_total >= 0 AND allocated_other_cost >= 0 AND effective_base_unit_cost >= 0 AND effective_line_cost >= 0 AND sale_price_at_purchase >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ProductNameSnapshot).HasMaxLength(250).IsRequired();
        builder.Property(x => x.SkuSnapshot).HasMaxLength(100);
        builder.Property(x => x.EnteredQuantity).HasPrecision(18, 6);
        builder.Property(x => x.FactorToBaseSnapshot).HasPrecision(18, 9);
        builder.Property(x => x.BaseQuantity).HasPrecision(18, 6);
        builder.Property(x => x.EnteredUnitCost).HasPrecision(18, 6);
        builder.Property(x => x.BaseLineTotal).HasPrecision(18, 2);
        builder.Property(x => x.AllocatedOtherCost).HasPrecision(18, 2);
        builder.Property(x => x.EffectiveBaseUnitCost).HasPrecision(18, 6);
        builder.Property(x => x.EffectiveLineCost).HasPrecision(18, 2);
        builder.Property(x => x.SalePriceAtPurchase).HasPrecision(18, 2);

        builder.HasOne<Purchase>()
            .WithMany()
            .HasForeignKey(x => x.PurchaseId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ProductUnit>()
            .WithMany()
            .HasForeignKey(x => x.ProductUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.PurchaseId);
        builder.HasIndex(x => x.ProductId);
    }
}

internal sealed class PurchaseItemUnitConfiguration : IEntityTypeConfiguration<PurchaseItemUnit>
{
    public void Configure(EntityTypeBuilder<PurchaseItemUnit> builder)
    {
        builder.ToTable("purchase_item_units", "purchasing");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.HasOne<PurchaseItem>()
            .WithMany()
            .HasForeignKey(x => x.PurchaseItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<InventoryUnit>()
            .WithMany()
            .HasForeignKey(x => x.InventoryUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.PurchaseItemId, x.InventoryUnitId }).IsUnique();
        builder.HasIndex(x => x.InventoryUnitId).IsUnique();
    }
}

internal sealed class PurchaseReturnConfiguration : IEntityTypeConfiguration<PurchaseReturn>
{
    public void Configure(EntityTypeBuilder<PurchaseReturn> builder)
    {
        builder.ToTable(
            "returns",
            "purchasing",
            table => table.HasCheckConstraint(
                "ck_purchase_return_values",
                "supplier_return_value >= 0 AND inventory_cost_removed >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ReturnNumber).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Note).HasMaxLength(1000);
        builder.Property(x => x.SupplierReturnValue).HasPrecision(18, 2);
        builder.Property(x => x.InventoryCostRemoved).HasPrecision(18, 6);

        builder.HasOne<Purchase>()
            .WithMany()
            .HasForeignKey(x => x.PurchaseId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.ReturnNumber).IsUnique();
        builder.HasIndex(x => x.ClientOperationId).IsUnique();
        builder.HasIndex(x => new { x.PurchaseId, x.CreatedAt });
    }
}

internal sealed class PurchaseReturnItemConfiguration : IEntityTypeConfiguration<PurchaseReturnItem>
{
    public void Configure(EntityTypeBuilder<PurchaseReturnItem> builder)
    {
        builder.ToTable(
            "return_items",
            "purchasing",
            table => table.HasCheckConstraint(
                "ck_purchase_return_item_values",
                "entered_quantity > 0 AND factor_to_base_snapshot > 0 AND base_quantity > 0 AND supplier_unit_return_value >= 0 AND supplier_return_value >= 0 AND inventory_unit_cost_removed >= 0 AND inventory_cost_removed >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.EnteredQuantity).HasPrecision(18, 6);
        builder.Property(x => x.FactorToBaseSnapshot).HasPrecision(18, 9);
        builder.Property(x => x.BaseQuantity).HasPrecision(18, 6);
        builder.Property(x => x.SupplierUnitReturnValue).HasPrecision(18, 6);
        builder.Property(x => x.SupplierReturnValue).HasPrecision(18, 2);
        builder.Property(x => x.InventoryUnitCostRemoved).HasPrecision(18, 6);
        builder.Property(x => x.InventoryCostRemoved).HasPrecision(18, 6);

        builder.HasOne<PurchaseReturn>()
            .WithMany()
            .HasForeignKey(x => x.PurchaseReturnId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PurchaseItem>()
            .WithMany()
            .HasForeignKey(x => x.PurchaseItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ProductUnit>()
            .WithMany()
            .HasForeignKey(x => x.ProductUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.PurchaseReturnId);
        builder.HasIndex(x => x.PurchaseItemId);
    }
}

internal sealed class PurchaseReturnItemUnitConfiguration : IEntityTypeConfiguration<PurchaseReturnItemUnit>
{
    public void Configure(EntityTypeBuilder<PurchaseReturnItemUnit> builder)
    {
        builder.ToTable("return_item_units", "purchasing");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.HasOne<PurchaseReturnItem>()
            .WithMany()
            .HasForeignKey(x => x.PurchaseReturnItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<InventoryUnit>()
            .WithMany()
            .HasForeignKey(x => x.InventoryUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.PurchaseReturnItemId, x.InventoryUnitId }).IsUnique();
    }
}

internal sealed class PurchaseVoidConfiguration : IEntityTypeConfiguration<PurchaseVoid>
{
    public void Configure(EntityTypeBuilder<PurchaseVoid> builder)
    {
        builder.ToTable(
            "purchase_voids",
            "purchasing",
            table => table.HasCheckConstraint(
                "ck_purchase_void_cash_reversal_nonnegative",
                "cash_drawer_reversal_amount IS NULL OR cash_drawer_reversal_amount >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.CashDrawerReversalAmount).HasPrecision(18, 2);

        builder.HasOne<Purchase>()
            .WithMany()
            .HasForeignKey(x => x.PurchaseId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.PurchaseId).IsUnique();
        builder.HasIndex(x => x.ClientOperationId).IsUnique();
    }
}

