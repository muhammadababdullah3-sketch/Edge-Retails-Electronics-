using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class PosDraftConfiguration : IEntityTypeConfiguration<PosDraft>
{
    public void Configure(EntityTypeBuilder<PosDraft> builder)
    {
        builder.ToTable("pos_drafts", "sales");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.DraftNumber).HasMaxLength(64).IsRequired();
        builder.Property(x => x.TerminalId).HasMaxLength(120);
        builder.Property(x => x.Note).HasMaxLength(1000);
        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.DraftNumber).IsUnique();
        builder.HasIndex(x => new { x.Status, x.UpdatedAt, x.Id });
        builder.HasIndex(x => new { x.CreatedBy, x.Status, x.UpdatedAt });
    }
}

internal sealed class PosDraftItemConfiguration : IEntityTypeConfiguration<PosDraftItem>
{
    public void Configure(EntityTypeBuilder<PosDraftItem> builder)
    {
        builder.ToTable(
            "pos_draft_items",
            "sales",
            table => table.HasCheckConstraint(
                "ck_pos_draft_item_values",
                "entered_quantity > 0 AND factor_to_base_snapshot > 0 AND base_quantity > 0 AND displayed_unit_price_snapshot >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.EnteredQuantity).HasPrecision(18, 6);
        builder.Property(x => x.FactorToBaseSnapshot).HasPrecision(18, 9);
        builder.Property(x => x.BaseQuantity).HasPrecision(18, 6);
        builder.Property(x => x.DisplayedUnitPriceSnapshot).HasPrecision(18, 2);
        builder.Property(x => x.Note).HasMaxLength(1000);

        builder.HasOne<PosDraft>().WithMany().HasForeignKey(x => x.DraftId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductUnit>().WithMany().HasForeignKey(x => x.ProductUnitId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<InventoryUnit>().WithMany().HasForeignKey(x => x.SelectedInventoryUnitId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.DraftId);
        builder.HasIndex(x => x.SelectedInventoryUnitId).HasFilter("selected_inventory_unit_id IS NOT NULL");
    }
}
