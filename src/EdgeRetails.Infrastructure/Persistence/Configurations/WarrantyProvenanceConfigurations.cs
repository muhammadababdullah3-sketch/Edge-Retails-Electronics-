using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Warranty;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class ShopWarrantySendAllocationConfiguration
    : IEntityTypeConfiguration<ShopWarrantySendAllocation>
{
    public void Configure(EntityTypeBuilder<ShopWarrantySendAllocation> builder)
    {
        builder.ToTable("shop_send_allocations", "warranty", table =>
        {
            table.HasCheckConstraint("ck_shop_send_allocation_quantity_positive", "base_quantity > 0");
            table.HasCheckConstraint("ck_shop_send_allocation_values_nonnegative",
                "source_unit_cost_snapshot >= 0 AND send_time_mwa_unit_cost_snapshot >= 0 AND send_time_carrying_value_snapshot >= 0");
            table.HasCheckConstraint("ck_shop_send_allocation_operation_required",
                "client_operation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.BaseQuantity).HasPrecision(18, 6);
        builder.Property(x => x.SourceUnitCostSnapshot).HasPrecision(18, 6);
        builder.Property(x => x.SendTimeMwaUnitCostSnapshot).HasPrecision(18, 6);
        builder.Property(x => x.SendTimeCarryingValueSnapshot).HasPrecision(18, 6);
        builder.HasOne<ShopStockWarrantyCase>().WithMany().HasForeignKey(x => x.CaseId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_shop_send_allocation_case");
        builder.HasOne<InventoryLot>().WithMany().HasForeignKey(x => x.OriginalInventoryLotId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_shop_send_allocation_original_lot");
        builder.HasOne<InventoryMovement>().WithMany().HasForeignKey(x => x.SendMovementId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_shop_send_allocation_movement");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.ActorId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_shop_send_allocation_actor");
        builder.HasIndex(x => new { x.CaseId, x.OriginalInventoryLotId, x.SendMovementId })
            .IsUnique().HasDatabaseName("ux_shop_send_allocation_source");
        builder.HasIndex(x => x.ClientOperationId).HasDatabaseName("ix_shop_send_allocation_operation");
    }
}

internal sealed class ShopWarrantyResolutionAllocationConfiguration
    : IEntityTypeConfiguration<ShopWarrantyResolutionAllocation>
{
    public void Configure(EntityTypeBuilder<ShopWarrantyResolutionAllocation> builder)
    {
        builder.ToTable("shop_resolution_allocations", "warranty", table =>
        {
            table.HasCheckConstraint("ck_shop_resolution_allocation_quantity_positive", "resolved_base_quantity > 0");
            table.HasCheckConstraint("ck_shop_resolution_allocation_values_nonnegative",
                "actual_resolved_carrying_value >= 0 AND (resolution_time_mwa_unit_cost_snapshot IS NULL OR resolution_time_mwa_unit_cost_snapshot >= 0) AND (supplier_credit_amount IS NULL OR supplier_credit_amount >= 0)");
            table.HasCheckConstraint("ck_shop_resolution_allocation_operation_required",
                "client_operation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_shop_resolution_allocation_outcome", "resolution_outcome IN (1, 2, 3, 4, 7)");
            table.HasCheckConstraint("ck_shop_resolution_allocation_valuation",
                "(resolution_outcome IN (1, 2, 3) AND actual_resolved_carrying_value = 0 AND resolution_time_mwa_unit_cost_snapshot IS NULL) OR (resolution_outcome IN (4, 7) AND resolution_time_mwa_unit_cost_snapshot IS NOT NULL)");
            table.HasCheckConstraint("ck_shop_resolution_allocation_credit",
                "(resolution_outcome = 4 AND supplier_credit_amount IS NOT NULL) OR (resolution_outcome <> 4 AND supplier_credit_amount IS NULL)");
            table.HasCheckConstraint("ck_shop_resolution_allocation_replacement",
                "replacement_inventory_lot_id IS NULL OR resolution_outcome = 2");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ResolvedBaseQuantity).HasPrecision(18, 6);
        builder.Property(x => x.ResolutionTimeMwaUnitCostSnapshot).HasPrecision(18, 6);
        builder.Property(x => x.ActualResolvedCarryingValue).HasPrecision(18, 6);
        builder.Property(x => x.SupplierCreditAmount).HasPrecision(18, 2);
        builder.HasOne<ShopWarrantySendAllocation>().WithMany().HasForeignKey(x => x.SendAllocationId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_shop_resolution_allocation_send");
        builder.HasOne<InventoryMovement>().WithMany().HasForeignKey(x => x.ResolutionMovementId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_shop_resolution_allocation_movement");
        builder.HasOne<InventoryLot>().WithMany().HasForeignKey(x => x.ReplacementInventoryLotId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_shop_resolution_allocation_replacement_lot");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.ActorId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_shop_resolution_allocation_actor");
        builder.HasIndex(x => new { x.SendAllocationId, x.ClientOperationId })
            .IsUnique().HasDatabaseName("ux_shop_resolution_allocation_operation");
        builder.HasIndex(x => x.ClientOperationId).HasDatabaseName("ix_shop_resolution_allocation_operation");
    }
}

internal sealed class WarrantyClaimSourceAllocationConfiguration
    : IEntityTypeConfiguration<WarrantyClaimSourceAllocation>
{
    public void Configure(EntityTypeBuilder<WarrantyClaimSourceAllocation> builder)
    {
        builder.ToTable("claim_source_allocations", "warranty", table =>
        {
            table.HasCheckConstraint("ck_claim_source_allocation_quantity_positive", "base_quantity > 0");
            table.HasCheckConstraint("ck_claim_source_allocation_operation_required",
                "client_operation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.BaseQuantity).HasPrecision(18, 6);
        builder.HasOne<WarrantyClaimItem>().WithMany().HasForeignKey(x => x.ClaimItemId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_claim_source_allocation_item");
        builder.HasOne<InventoryLotConsumption>().WithMany().HasForeignKey(x => x.SaleConsumptionId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_claim_source_allocation_consumption");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.ActorId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_claim_source_allocation_actor");
        builder.HasIndex(x => new { x.ClaimItemId, x.SaleConsumptionId })
            .IsUnique().HasDatabaseName("ux_claim_source_allocation_consumption");
        builder.HasIndex(x => x.ClientOperationId).HasDatabaseName("ix_claim_source_allocation_operation");
    }
}

internal sealed class SaleReturnSourceAllocationConfiguration
    : IEntityTypeConfiguration<SaleReturnSourceAllocation>
{
    public void Configure(EntityTypeBuilder<SaleReturnSourceAllocation> builder)
    {
        builder.ToTable("sale_return_source_allocations", "warranty", table =>
        {
            table.HasCheckConstraint("ck_sale_return_source_allocation_quantity_positive", "base_quantity > 0");
            table.HasCheckConstraint("ck_sale_return_source_allocation_operation_required",
                "client_operation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.BaseQuantity).HasPrecision(18, 6);
        builder.HasOne<SaleReturnItem>().WithMany().HasForeignKey(x => x.SaleReturnItemId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sale_return_source_allocation_item");
        builder.HasOne<InventoryLotConsumption>().WithMany().HasForeignKey(x => x.SaleConsumptionId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sale_return_source_allocation_consumption");
        builder.HasOne<InventoryMovement>().WithMany().HasForeignKey(x => x.ReturnMovementId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sale_return_source_allocation_movement");
        builder.HasOne<InventoryLot>().WithMany().HasForeignKey(x => x.RestoredInventoryLotId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sale_return_source_allocation_restored_lot");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.ActorId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sale_return_source_allocation_actor");
        builder.HasIndex(x => new { x.SaleReturnItemId, x.SaleConsumptionId })
            .IsUnique().HasDatabaseName("ux_sale_return_source_allocation_consumption");
        builder.HasIndex(x => x.RestoredInventoryLotId).IsUnique()
            .HasFilter("restored_inventory_lot_id IS NOT NULL")
            .HasDatabaseName("ux_sale_return_source_allocation_restored_lot");
        builder.HasIndex(x => x.ClientOperationId).HasDatabaseName("ix_sale_return_source_allocation_operation");
    }
}
