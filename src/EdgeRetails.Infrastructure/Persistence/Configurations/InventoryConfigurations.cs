using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Warranty;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class StockBalanceConfiguration : IEntityTypeConfiguration<StockBalance>
{
    public void Configure(EntityTypeBuilder<StockBalance> builder)
    {
        builder.ToTable(
            "stock_balances",
            "inventory",
            table => table.HasCheckConstraint(
                "ck_stock_balances_nonnegative",
                "sellable_qty >= 0 AND damaged_qty >= 0 AND defective_qty >= 0 AND with_supplier_qty >= 0 AND scrap_qty >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.SellableQty).HasPrecision(18, 6);
        builder.Property(x => x.DamagedQty).HasPrecision(18, 6);
        builder.Property(x => x.DefectiveQty).HasPrecision(18, 6);
        builder.Property(x => x.WithSupplierQty).HasPrecision(18, 6);
        builder.Property(x => x.ScrapQty).HasPrecision(18, 6);
        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.ProductId).IsUnique();
    }
}

internal sealed class ProductCostStateConfiguration
    : IEntityTypeConfiguration<ProductCostState>
{
    public void Configure(EntityTypeBuilder<ProductCostState> builder)
    {
        builder.ToTable(
            "cost_states",
            "inventory",
            table => table.HasCheckConstraint(
                "ck_cost_states_nonnegative",
                "costed_qty >= 0 AND total_inventory_cost >= 0 AND moving_average_cost >= 0 AND (last_purchase_cost IS NULL OR last_purchase_cost >= 0)"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.CostedQty).HasPrecision(18, 6);
        builder.Property(x => x.TotalInventoryCost).HasPrecision(18, 6);
        builder.Property(x => x.MovingAverageCost).HasPrecision(18, 6);
        builder.Property(x => x.LastPurchaseCost).HasPrecision(18, 6);
        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.ProductId).IsUnique();
    }
}

internal sealed class InventoryMovementConfiguration
    : IEntityTypeConfiguration<InventoryMovement>
{
    public void Configure(EntityTypeBuilder<InventoryMovement> builder)
    {
        builder.ToTable(
            "movements",
            "inventory",
            table => table.HasCheckConstraint(
                "ck_inventory_movement_loss_nonnegative",
                "recognized_loss_amount >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ReferenceType).HasMaxLength(80).IsRequired();
        builder.Property(x => x.UnitCostSnapshot).HasPrecision(18, 6);
        builder.Property(x => x.RecognizedLossAmount).HasPrecision(18, 2);
        builder.Property(x => x.Reason).HasMaxLength(160);
        builder.Property(x => x.Note).HasMaxLength(1000);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.ProductId, x.OccurredAt });
        builder.HasIndex(x => new { x.OccurredAt, x.Id })
            .IsDescending(true, true);
        builder.HasIndex(x => new { x.ReferenceType, x.ReferenceId });
        builder.HasIndex(x => x.CorrelationId);
    }
}

internal sealed class InventoryMovementEffectConfiguration
    : IEntityTypeConfiguration<InventoryMovementEffect>
{
    public void Configure(EntityTypeBuilder<InventoryMovementEffect> builder)
    {
        builder.ToTable(
            "movement_effects",
            "inventory",
            table => table.HasCheckConstraint(
                "ck_inventory_movement_effect_snapshots_nonnegative",
                "quantity_before >= 0 AND quantity_after >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.QuantityDelta).HasPrecision(18, 6);
        builder.Property(x => x.QuantityBefore).HasPrecision(18, 6);
        builder.Property(x => x.QuantityAfter).HasPrecision(18, 6);

        builder.HasOne<InventoryMovement>()
            .WithMany()
            .HasForeignKey(x => x.MovementId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.MovementId);
    }
}

internal sealed class InventoryUnitConfiguration : IEntityTypeConfiguration<InventoryUnit>
{
    public void Configure(EntityTypeBuilder<InventoryUnit> builder)
    {
        builder.ToTable(
            "units",
            "inventory",
            table =>
            {
                table.HasCheckConstraint("ck_inventory_unit_cost_nonnegative", "acquisition_cost >= 0");
                table.HasCheckConstraint("ck_inventory_unit_sequence_positive", "item_sequence IS NULL OR item_sequence >= 1");
                table.HasCheckConstraint(
                    "ck_inventory_units_origin_provenance",
                    "(origin_type = 1 AND source_purchase_item_id IS NOT NULL AND source_warranty_claim_item_id IS NULL AND source_warranty_case_id IS NULL AND source_stock_adjustment_item_id IS NULL) OR " +
                    "(origin_type = 2 AND source_purchase_item_id IS NULL AND source_stock_adjustment_item_id IS NULL AND ((source_warranty_claim_item_id IS NOT NULL AND source_warranty_case_id IS NULL) OR (source_warranty_claim_item_id IS NULL AND source_warranty_case_id IS NOT NULL))) OR " +
                    "(origin_type = 3 AND source_stock_adjustment_item_id IS NOT NULL AND source_purchase_item_id IS NULL AND source_warranty_claim_item_id IS NULL AND source_warranty_case_id IS NULL)");
            });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.TrackingCode).HasMaxLength(320);
        builder.Property(x => x.SupplierCodeSnapshot).HasMaxLength(32);
        builder.Property(x => x.ProductSkuSnapshot).HasMaxLength(100);
        builder.Property(x => x.SerialNumber).HasMaxLength(160);
        builder.Property(x => x.Imei1).HasMaxLength(40);
        builder.Property(x => x.Imei2).HasMaxLength(40);
        builder.Property(x => x.AcquisitionCost).HasPrecision(18, 6);
        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SupplierProduct>()
            .WithMany()
            .HasForeignKey(x => x.SupplierProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<InventoryLot>()
            .WithMany()
            .HasForeignKey(x => x.InventoryLotId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PurchaseItem>()
            .WithMany()
            .HasForeignKey(x => x.SourcePurchaseItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<WarrantyClaimItem>()
            .WithMany()
            .HasForeignKey(x => x.SourceWarrantyClaimItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<StockAdjustmentItem>()
            .WithMany()
            .HasForeignKey(x => x.SourceStockAdjustmentItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.InventoryLotId);
        builder.HasIndex(x => x.SourcePurchaseItemId);
        builder.HasIndex(x => x.SourceWarrantyClaimItemId);
        builder.HasIndex(x => x.SourceStockAdjustmentItemId);
        builder.HasIndex(x => x.SupplierProductId);
        builder.HasIndex(x => x.TrackingCode)
            .IsUnique()
            .HasFilter("tracking_code IS NOT NULL");
        builder.HasIndex(x => new { x.SupplierProductId, x.ItemSequence })
            .IsUnique()
            .HasFilter("supplier_product_id IS NOT NULL AND item_sequence IS NOT NULL");
        builder.HasIndex(x => x.SerialNumber)
            .IsUnique()
            .HasFilter("serial_number IS NOT NULL");
        builder.HasIndex(x => x.Imei1)
            .IsUnique()
            .HasFilter("imei1 IS NOT NULL");
        builder.HasIndex(x => x.Imei2)
            .IsUnique()
            .HasFilter("imei2 IS NOT NULL");
        builder.HasIndex(x => new { x.ProductId, x.Status });
    }
}

internal sealed class InventoryMovementUnitConfiguration
    : IEntityTypeConfiguration<InventoryMovementUnit>
{
    public void Configure(EntityTypeBuilder<InventoryMovementUnit> builder)
    {
        builder.ToTable("movement_units", "inventory");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.HasOne<InventoryMovement>()
            .WithMany()
            .HasForeignKey(x => x.MovementId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<InventoryUnit>()
            .WithMany()
            .HasForeignKey(x => x.InventoryUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.MovementId, x.InventoryUnitId }).IsUnique();
    }
}

internal sealed class InventoryLotConfiguration : IEntityTypeConfiguration<InventoryLot>
{
    public void Configure(EntityTypeBuilder<InventoryLot> builder)
    {
        builder.ToTable(
            "lots",
            "inventory",
            table => table.HasCheckConstraint(
                "ck_inventory_lots_values",
                "received_quantity > 0 AND original_unit_cost >= 0 AND effective_unit_cost >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ReceivedQuantity).HasPrecision(18, 6);
        builder.Property(x => x.OriginalUnitCost).HasPrecision(18, 6);
        builder.Property(x => x.EffectiveUnitCost).HasPrecision(18, 6);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<InventoryMovement>()
            .WithMany()
            .HasForeignKey(x => x.SourceMovementId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PurchaseItem>()
            .WithMany()
            .HasForeignKey(x => x.PurchaseItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.ProductId, x.CreatedAt });
        builder.HasIndex(x => x.PurchaseItemId);
    }
}

internal sealed class InventoryLotBucketBalanceConfiguration
    : IEntityTypeConfiguration<InventoryLotBucketBalance>
{
    public void Configure(EntityTypeBuilder<InventoryLotBucketBalance> builder)
    {
        builder.ToTable(
            "lot_bucket_balances",
            "inventory",
            table => table.HasCheckConstraint(
                "ck_inventory_lot_bucket_quantity_nonnegative",
                "quantity >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Quantity).HasPrecision(18, 6);

        builder.HasOne<InventoryLot>()
            .WithMany()
            .HasForeignKey(x => x.LotId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.LotId, x.StockBucket }).IsUnique();
    }
}

internal sealed class InventoryLotConsumptionConfiguration
    : IEntityTypeConfiguration<InventoryLotConsumption>
{
    public void Configure(EntityTypeBuilder<InventoryLotConsumption> builder)
    {
        builder.ToTable(
            "lot_consumptions",
            "inventory",
            table => table.HasCheckConstraint(
                "ck_inventory_lot_consumptions_values",
                "quantity > 0 AND unit_cost_snapshot >= 0 AND total_cost_snapshot >= 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Quantity).HasPrecision(18, 6);
        builder.Property(x => x.UnitCostSnapshot).HasPrecision(18, 6);
        builder.Property(x => x.TotalCostSnapshot).HasPrecision(18, 6);

        builder.HasOne<InventoryLot>()
            .WithMany()
            .HasForeignKey(x => x.LotId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<InventoryMovement>()
            .WithMany()
            .HasForeignKey(x => x.MovementId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.LotId);
        builder.HasIndex(x => x.MovementId);
        builder.HasIndex(x => new { x.LotId, x.MovementId });
    }
}

internal sealed class StocktakeConfiguration : IEntityTypeConfiguration<Stocktake>
{
    public void Configure(EntityTypeBuilder<Stocktake> builder)
    {
        builder.ToTable("stocktakes", "inventory");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Note).HasMaxLength(1000);
        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.Status, x.CreatedAt });

        builder.HasIndex(x => x.Status)
            .HasDatabaseName("ux_stocktakes_single_open")
            .IsUnique()
            .HasFilter("status IN (1, 2, 3)");
    }
}

internal sealed class StocktakeItemConfiguration : IEntityTypeConfiguration<StocktakeItem>
{
    public void Configure(EntityTypeBuilder<StocktakeItem> builder)
    {
        builder.ToTable(
            "stocktake_items",
            "inventory",
            table => table.HasCheckConstraint(
                "ck_stocktake_items_nonnegative",
                "expected_sellable_qty >= 0 AND (counted_sellable_qty IS NULL OR counted_sellable_qty >= 0)"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ExpectedSellableQty).HasPrecision(18, 6);
        builder.Property(x => x.CountedSellableQty).HasPrecision(18, 6);
        builder.Property(x => x.ReviewNote).HasMaxLength(1000);
        builder.Ignore(x => x.VarianceQty);

        builder.HasOne<Stocktake>()
            .WithMany()
            .HasForeignKey(x => x.StocktakeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.StocktakeId, x.ProductId }).IsUnique();
    }
}

internal sealed class StocktakeUnitCheckConfiguration
    : IEntityTypeConfiguration<StocktakeUnitCheck>
{
    public void Configure(EntityTypeBuilder<StocktakeUnitCheck> builder)
    {
        builder.ToTable("stocktake_unit_checks", "inventory");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.IdentitySnapshot).HasMaxLength(320).IsRequired();
        builder.Property(x => x.Note).HasMaxLength(1000);

        builder.HasOne<StocktakeItem>()
            .WithMany()
            .HasForeignKey(x => x.StocktakeItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<InventoryUnit>()
            .WithMany()
            .HasForeignKey(x => x.InventoryUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.StocktakeItemId, x.InventoryUnitId })
            .IsUnique()
            .HasFilter("inventory_unit_id IS NOT NULL");
    }
}

internal sealed class StockAdjustmentConfiguration : IEntityTypeConfiguration<StockAdjustment>
{
    public void Configure(EntityTypeBuilder<StockAdjustment> builder)
    {
        builder.ToTable("stock_adjustments", "inventory");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.AdjustmentNumber).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Note).HasMaxLength(1000);
        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasIndex(x => x.AdjustmentNumber).IsUnique();
        builder.HasIndex(x => new { x.OccurredAt, x.Status });
        builder.HasIndex(x => x.CorrelationId);
    }
}

internal sealed class StockAdjustmentItemConfiguration : IEntityTypeConfiguration<StockAdjustmentItem>
{
    public void Configure(EntityTypeBuilder<StockAdjustmentItem> builder)
    {
        builder.ToTable(
            "stock_adjustment_items",
            "inventory",
            table => table.HasCheckConstraint(
                "ck_stock_adjustment_items_base_qty_positive",
                "base_quantity > 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.BaseQuantity).HasPrecision(18, 6);
        builder.Property(x => x.UnitCostSnapshot).HasPrecision(18, 6);
        builder.Property(x => x.TotalCostSnapshot).HasPrecision(18, 6);
        builder.Property(x => x.ReasonDetails).HasMaxLength(500);

        builder.HasOne<StockAdjustment>()
            .WithMany()
            .HasForeignKey(x => x.StockAdjustmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(x => x.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SupplierProduct>()
            .WithMany()
            .HasForeignKey(x => x.SupplierProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.StockAdjustmentId);
        builder.HasIndex(x => x.ProductId);
        builder.HasIndex(x => x.SupplierId);
        builder.HasIndex(x => x.SupplierProductId);
    }
}
