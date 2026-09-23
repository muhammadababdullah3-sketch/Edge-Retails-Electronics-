using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Warranty;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class WarrantyClaimConfiguration : IEntityTypeConfiguration<WarrantyClaim>
{
    public void Configure(EntityTypeBuilder<WarrantyClaim> builder)
    {
        builder.ToTable("claims", "warranty");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ClaimNumber).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasIndex(x => x.ClaimNumber).IsUnique();
        builder.HasIndex(x => x.ClientOperationId).IsUnique();
        builder.HasIndex(x => new { x.CustomerId, x.Status, x.ReceivedAt });

        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(x => x.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WarrantyClaimItemConfiguration
    : IEntityTypeConfiguration<WarrantyClaimItem>
{
    public void Configure(EntityTypeBuilder<WarrantyClaimItem> builder)
    {
        builder.ToTable(
            "claim_items",
            "warranty",
            table => table.HasCheckConstraint(
                "ck_warranty_claim_item_quantity_positive",
                "quantity > 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Quantity).HasPrecision(18, 6);
        builder.Property(x => x.FaultDescription).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.ResolutionNote).HasMaxLength(1000);
        builder.Property(x => x.ReplacementReference).HasMaxLength(200);

        builder.HasOne<WarrantyClaim>()
            .WithMany()
            .HasForeignKey(x => x.ClaimId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ReplacementProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.ClaimId);
    }
}

internal sealed class WarrantyClaimItemUnitConfiguration
    : IEntityTypeConfiguration<WarrantyClaimItemUnit>
{
    public void Configure(EntityTypeBuilder<WarrantyClaimItemUnit> builder)
    {
        builder.ToTable("claim_item_units", "warranty");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.OriginalIdentitySnapshot).HasMaxLength(320);
        builder.Property(x => x.ReplacementIdentitySnapshot).HasMaxLength(320);

        builder.HasOne<WarrantyClaimItem>()
            .WithMany()
            .HasForeignKey(x => x.ClaimItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<InventoryUnit>()
            .WithMany()
            .HasForeignKey(x => x.OriginalInventoryUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<InventoryUnit>()
            .WithMany()
            .HasForeignKey(x => x.ActiveOriginalInventoryUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<InventoryUnit>()
            .WithMany()
            .HasForeignKey(x => x.ReplacementInventoryUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.ClaimItemId);
        builder.HasIndex(x => x.OriginalInventoryUnitId)
            .HasFilter("original_inventory_unit_id IS NOT NULL");
        builder.HasIndex(x => x.ActiveOriginalInventoryUnitId)
            .IsUnique()
            .HasFilter("active_original_inventory_unit_id IS NOT NULL");
        builder.HasIndex(x => x.ReplacementInventoryUnitId)
            .HasFilter("replacement_inventory_unit_id IS NOT NULL");
    }
}

internal sealed class WarrantyClaimEventConfiguration
    : IEntityTypeConfiguration<WarrantyClaimEvent>
{
    public void Configure(EntityTypeBuilder<WarrantyClaimEvent> builder)
    {
        builder.ToTable("claim_events", "warranty");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Note).HasMaxLength(1000);

        builder.HasOne<WarrantyClaim>()
            .WithMany()
            .HasForeignKey(x => x.ClaimId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.ClaimId, x.OccurredAt });
    }
}

internal sealed class WarrantyOperationConfiguration
    : IEntityTypeConfiguration<WarrantyOperation>
{
    public void Configure(EntityTypeBuilder<WarrantyOperation> builder)
    {
        builder.ToTable("operations", "warranty");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.TargetType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.OperationType).IsRequired();
        builder.Property(x => x.ResultId);
        builder.HasIndex(x => x.ClientOperationId).IsUnique();
        builder.HasIndex(x => new { x.TargetType, x.TargetId, x.OperationType, x.OccurredAt });
    }
}

internal sealed class ShopStockWarrantyCaseConfiguration
    : IEntityTypeConfiguration<ShopStockWarrantyCase>
{
    public void Configure(EntityTypeBuilder<ShopStockWarrantyCase> builder)
    {
        builder.ToTable(
            "shop_stock_cases",
            "warranty",
            table => table.HasCheckConstraint(
                "ck_shop_warranty_quantity_positive",
                "base_quantity > 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.CaseNumber).HasMaxLength(64).IsRequired();
        builder.Property(x => x.BaseQuantity).HasPrecision(18, 6);
        builder.Property(x => x.FaultDescription).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.SupplierReference).HasMaxLength(200);
        builder.Property(x => x.InventoryCarryingCostResolved).HasPrecision(18, 6);
        builder.Property(x => x.SupplierCreditAmount).HasPrecision(18, 2);
        builder.Property(x => x.RecoveryDifference).HasPrecision(18, 6);
        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(x => x.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany<InventoryUnit>()
            .WithOne()
            .HasForeignKey(x => x.SourceWarrantyCaseId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.CaseNumber).IsUnique();
        builder.HasIndex(x => x.ResolutionClientOperationId)
            .IsUnique()
            .HasFilter("resolution_client_operation_id IS NOT NULL");
        builder.HasIndex(x => new { x.SupplierId, x.Status, x.CreatedAt });
    }
}
