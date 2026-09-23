using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class QuotationConfiguration : IEntityTypeConfiguration<Quotation>
{
    public void Configure(EntityTypeBuilder<Quotation> builder)
    {
        builder.ToTable(
            "quotations",
            "sales",
            table => table.HasCheckConstraint(
                "ck_quotation_totals",
                "subtotal >= 0 AND discount >= 0 AND discount <= subtotal AND grand_total >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.QuotationNumber).HasMaxLength(64).IsRequired();
        builder.Property(x => x.CustomerNameSnapshot).HasMaxLength(200);
        builder.Property(x => x.Subtotal).HasPrecision(18, 2);
        builder.Property(x => x.Discount).HasPrecision(18, 2);
        builder.Property(x => x.GrandTotal).HasPrecision(18, 2);
        builder.Property(x => x.Notes).HasMaxLength(2000);
        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.QuotationNumber).IsUnique();
        builder.HasIndex(x => new { x.Status, x.QuotationDate });
        builder.HasIndex(x => new { x.CustomerId, x.QuotationDate });
    }
}

internal sealed class QuotationItemConfiguration : IEntityTypeConfiguration<QuotationItem>
{
    public void Configure(EntityTypeBuilder<QuotationItem> builder)
    {
        builder.ToTable(
            "quotation_items",
            "sales",
            table => table.HasCheckConstraint(
                "ck_quotation_item_values",
                "entered_quantity > 0 AND factor_to_base_snapshot > 0 AND base_quantity > 0 AND quoted_unit_price >= 0 AND line_total >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ProductName).HasMaxLength(250).IsRequired();
        builder.Property(x => x.Sku).HasMaxLength(100);
        builder.Property(x => x.EnteredQuantity).HasPrecision(18, 6);
        builder.Property(x => x.FactorToBaseSnapshot).HasPrecision(18, 9);
        builder.Property(x => x.BaseQuantity).HasPrecision(18, 6);
        builder.Property(x => x.QuotedUnitPrice).HasPrecision(18, 2);
        builder.Property(x => x.LineTotal).HasPrecision(18, 2);

        builder.HasOne<Quotation>()
            .WithMany()
            .HasForeignKey(x => x.QuotationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Unit>()
            .WithMany()
            .HasForeignKey(x => x.SelectedUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.QuotationId);
    }
}


internal sealed class QuotationOperationConfiguration
    : IEntityTypeConfiguration<QuotationOperation>
{
    public void Configure(EntityTypeBuilder<QuotationOperation> builder)
    {
        builder.ToTable("quotation_operations", "sales");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.HasOne<Quotation>()
            .WithMany()
            .HasForeignKey(x => x.QuotationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.ClientOperationId).IsUnique();
        builder.HasIndex(x => new { x.QuotationId, x.OccurredAt });
    }
}

internal sealed class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> builder)
    {
        builder.ToTable(
            "sales",
            "sales",
            table => table.HasCheckConstraint(
                "ck_sales_totals",
                "subtotal >= 0 AND invoice_discount >= 0 AND grand_total >= 0 AND invoice_discount <= subtotal"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.InvoiceNumber).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Subtotal).HasPrecision(18, 2);
        builder.Property(x => x.InvoiceDiscount).HasPrecision(18, 2);
        builder.Property(x => x.GrandTotal).HasPrecision(18, 2);
        builder.Property(x => x.ReceiptTemplateSnapshot).HasColumnType("text");

        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.InvoiceNumber).IsUnique();
        builder.HasIndex(x => x.ClientOperationId).IsUnique();
        builder.HasIndex(x => x.CompletedAt);
        builder.HasIndex(x => x.CustomerId);
    }
}

internal sealed class SaleItemConfiguration : IEntityTypeConfiguration<SaleItem>
{
    public void Configure(EntityTypeBuilder<SaleItem> builder)
    {
        builder.ToTable(
            "sale_items",
            "sales",
            table => table.HasCheckConstraint(
                "ck_sale_item_values",
                "entered_quantity > 0 AND factor_to_base_snapshot > 0 AND base_quantity > 0 AND unit_price >= 0 AND gross_line_total >= 0 AND allocated_invoice_discount >= 0 AND net_line_total >= 0 AND unit_cost_snapshot >= 0 AND total_cost_snapshot >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ProductNameSnapshot).HasMaxLength(250).IsRequired();
        builder.Property(x => x.SkuSnapshot).HasMaxLength(100);
        builder.Property(x => x.EnteredQuantity).HasPrecision(18, 6);
        builder.Property(x => x.FactorToBaseSnapshot).HasPrecision(18, 9);
        builder.Property(x => x.BaseQuantity).HasPrecision(18, 6);
        builder.Property(x => x.UnitPrice).HasPrecision(18, 2);
        builder.Property(x => x.GrossLineTotal).HasPrecision(18, 2);
        builder.Property(x => x.AllocatedInvoiceDiscount).HasPrecision(18, 2);
        builder.Property(x => x.NetLineTotal).HasPrecision(18, 2);
        builder.Property(x => x.UnitCostSnapshot).HasPrecision(18, 6);
        builder.Property(x => x.TotalCostSnapshot).HasPrecision(18, 6);
        builder.Property(x => x.GrossProfitSnapshot).HasPrecision(18, 2);
        builder.Property(x => x.WarrantyValidUntil).HasColumnType("date");

        builder.HasOne<Sale>()
            .WithMany()
            .HasForeignKey(x => x.SaleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<InventoryMovement>()
            .WithMany()
            .HasForeignKey(x => x.InventoryMovementId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ProductUnit>()
            .WithMany()
            .HasForeignKey(x => x.ProductUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.SaleId);
        builder.HasIndex(x => x.ProductId);
    }
}

internal sealed class SalePaymentConfiguration : IEntityTypeConfiguration<SalePayment>
{
    public void Configure(EntityTypeBuilder<SalePayment> builder)
    {
        builder.ToTable(
            "sale_payments",
            "sales",
            table => table.HasCheckConstraint(
                "ck_sale_payment_values",
                "amount_tendered >= 0 AND applied_amount >= 0 AND change_given >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.AmountTendered).HasPrecision(18, 2);
        builder.Property(x => x.AppliedAmount).HasPrecision(18, 2);
        builder.Property(x => x.ChangeGiven).HasPrecision(18, 2);
        builder.Property(x => x.Reference).HasMaxLength(250);

        builder.HasOne<Sale>()
            .WithMany()
            .HasForeignKey(x => x.SaleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.SaleId).IsUnique();
    }
}

internal sealed class SaleItemUnitConfiguration : IEntityTypeConfiguration<SaleItemUnit>
{
    public void Configure(EntityTypeBuilder<SaleItemUnit> builder)
    {
        builder.ToTable("sale_item_units", "sales");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.UnitCostSnapshot).HasPrecision(18, 6);

        builder.HasOne<SaleItem>()
            .WithMany()
            .HasForeignKey(x => x.SaleItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<InventoryUnit>()
            .WithMany()
            .HasForeignKey(x => x.InventoryUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.SaleItemId, x.InventoryUnitId }).IsUnique();
        builder.HasIndex(x => x.InventoryUnitId);
    }
}

internal sealed class SaleReturnConfiguration : IEntityTypeConfiguration<SaleReturn>
{
    public void Configure(EntityTypeBuilder<SaleReturn> builder)
    {
        builder.ToTable(
            "returns",
            "sales",
            table => table.HasCheckConstraint(
                "ck_sale_return_refund_nonnegative",
                "refund_amount >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ReturnNumber).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ReasonCode).HasMaxLength(80).IsRequired();
        builder.Property(x => x.ReasonNote).HasMaxLength(1000);
        builder.Property(x => x.RefundAmount).HasPrecision(18, 2);

        builder.HasOne<Sale>()
            .WithMany()
            .HasForeignKey(x => x.SaleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.ReturnNumber).IsUnique();
        builder.HasIndex(x => x.ClientOperationId).IsUnique();
        builder.HasIndex(x => new { x.SaleId, x.CreatedAt });
    }
}

internal sealed class SaleReturnItemConfiguration : IEntityTypeConfiguration<SaleReturnItem>
{
    public void Configure(EntityTypeBuilder<SaleReturnItem> builder)
    {
        builder.ToTable(
            "return_items",
            "sales",
            table => table.HasCheckConstraint(
                "ck_sale_return_item_values",
                "entered_quantity > 0 AND factor_to_base_snapshot > 0 AND base_quantity > 0 AND refund_amount >= 0 AND original_cost_amount >= 0 AND cost_reversal_amount >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.EnteredQuantity).HasPrecision(18, 6);
        builder.Property(x => x.FactorToBaseSnapshot).HasPrecision(18, 9);
        builder.Property(x => x.BaseQuantity).HasPrecision(18, 6);
        builder.Property(x => x.RefundAmount).HasPrecision(18, 2);
        builder.Property(x => x.OriginalCostAmount).HasPrecision(18, 6);
        builder.Property(x => x.CostReversalAmount).HasPrecision(18, 6);

        builder.HasOne<SaleReturn>()
            .WithMany()
            .HasForeignKey(x => x.SaleReturnId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SaleItem>()
            .WithMany()
            .HasForeignKey(x => x.SaleItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ProductUnit>()
            .WithMany()
            .HasForeignKey(x => x.ProductUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.SaleReturnId);
        builder.HasIndex(x => x.SaleItemId);
    }
}

internal sealed class SaleReturnItemUnitConfiguration : IEntityTypeConfiguration<SaleReturnItemUnit>
{
    public void Configure(EntityTypeBuilder<SaleReturnItemUnit> builder)
    {
        builder.ToTable("return_item_units", "sales");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.HasOne<SaleReturnItem>()
            .WithMany()
            .HasForeignKey(x => x.SaleReturnItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<InventoryUnit>()
            .WithMany()
            .HasForeignKey(x => x.InventoryUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.SaleReturnItemId, x.InventoryUnitId }).IsUnique();
    }
}

