using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Thaka;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class ThakaProjectConfiguration : IEntityTypeConfiguration<ThakaProject>
{
    public void Configure(EntityTypeBuilder<ThakaProject> builder)
    {
        builder.ToTable("projects", "thaka");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ProjectNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.ProjectName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.SiteAddress).HasMaxLength(500);
        builder.Property(x => x.Note).HasMaxLength(1000);
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.ProjectNumber).IsUnique();
        builder.HasIndex(x => new { x.Status, x.StartedOn });
    }
}

internal sealed class ThakaMaterialIssueConfiguration
    : IEntityTypeConfiguration<ThakaMaterialIssue>
{
    public void Configure(EntityTypeBuilder<ThakaMaterialIssue> builder)
    {
        builder.ToTable(
            "material_issues",
            "thaka",
            table => table.HasCheckConstraint(
                "ck_thaka_issue_values",
                "total_charge >= 0 AND total_cost >= 0 AND gross_profit = total_charge - total_cost"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ChallanNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.TotalCharge).HasPrecision(18, 2);
        builder.Property(x => x.TotalCost).HasPrecision(18, 6);
        builder.Property(x => x.GrossProfit).HasPrecision(18, 2);
        builder.Property(x => x.Note).HasMaxLength(1000);
        builder.HasOne<ThakaProject>()
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.ChallanNumber).IsUnique();
        builder.HasIndex(x => x.ClientOperationId).IsUnique();
        builder.HasIndex(x => new { x.ProjectId, x.IssuedAt });
    }
}

internal sealed class ThakaMaterialIssueItemConfiguration
    : IEntityTypeConfiguration<ThakaMaterialIssueItem>
{
    public void Configure(EntityTypeBuilder<ThakaMaterialIssueItem> builder)
    {
        builder.ToTable(
            "material_issue_items",
            "thaka",
            table => table.HasCheckConstraint(
                "ck_thaka_issue_item_values",
                "entered_quantity > 0 AND factor_to_base_snapshot > 0 AND base_quantity > 0 AND unit_charge >= 0 AND line_charge >= 0 AND unit_cost_snapshot >= 0 AND total_cost_snapshot >= 0")); builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ProductNameSnapshot).HasMaxLength(200).IsRequired();
        builder.Property(x => x.SkuSnapshot).HasMaxLength(120);
        builder.Property(x => x.EnteredQuantity).HasPrecision(18, 6);
        builder.Property(x => x.FactorToBaseSnapshot).HasPrecision(18, 9);
        builder.Property(x => x.BaseQuantity).HasPrecision(18, 6);
        builder.Property(x => x.UnitCharge).HasPrecision(18, 2);
        builder.Property(x => x.LineCharge).HasPrecision(18, 2);
        builder.Property(x => x.UnitCostSnapshot).HasPrecision(18, 6);
        builder.Property(x => x.TotalCostSnapshot).HasPrecision(18, 6);
        builder.Property(x => x.GrossProfitSnapshot).HasPrecision(18, 2);

        builder.HasOne<ThakaMaterialIssue>()
            .WithMany()
            .HasForeignKey(x => x.MaterialIssueId)
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
        builder.HasIndex(x => x.MaterialIssueId);
    }
}
internal sealed class ThakaMaterialIssueUnitConfiguration
    : IEntityTypeConfiguration<ThakaMaterialIssueUnit>
{
    public void Configure(EntityTypeBuilder<ThakaMaterialIssueUnit> builder)
    {
        builder.ToTable("material_issue_units", "thaka");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.UnitCostSnapshot).HasPrecision(18, 6);
        builder.HasOne<ThakaMaterialIssueItem>()
            .WithMany()
            .HasForeignKey(x => x.MaterialIssueItemId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<InventoryUnit>()
            .WithMany()
            .HasForeignKey(x => x.InventoryUnitId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.MaterialIssueItemId, x.InventoryUnitId }).IsUnique();
    }
}

internal sealed class ThakaPaymentConfiguration : IEntityTypeConfiguration<ThakaPayment>
{
    public void Configure(EntityTypeBuilder<ThakaPayment> builder)
    {
        builder.ToTable(
            "payments",
            "thaka",
            table => table.HasCheckConstraint(
                "ck_thaka_payment_amount_positive",
                "amount > 0"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ReceiptNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Amount).HasPrecision(18, 2);
        builder.Property(x => x.Reference).HasMaxLength(160);
        builder.Property(x => x.Note).HasMaxLength(1000); builder.HasOne<ThakaProject>()
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CashSession>()
            .WithMany()
            .HasForeignKey(x => x.CashSessionId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.ReceiptNumber).IsUnique();
        builder.HasIndex(x => x.ClientOperationId).IsUnique();
        builder.HasIndex(x => new { x.ProjectId, x.RecordedAt });
    }
}

internal sealed class ThakaSettlementConfiguration
    : IEntityTypeConfiguration<ThakaSettlement>
{
    public void Configure(EntityTypeBuilder<ThakaSettlement> builder)
    {
        builder.ToTable(
            "settlements",
            "thaka",
            table => table.HasCheckConstraint(
                "ck_thaka_settlement_values",
                "gross_material_charges_snapshot >= 0 AND payments_collected_snapshot >= 0 AND settlement_discount >= 0 AND final_payment_amount >= 0 AND balance_before_settlement >= 0"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.SettlementNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.GrossMaterialChargesSnapshot).HasPrecision(18, 2);
        builder.Property(x => x.PaymentsCollectedSnapshot).HasPrecision(18, 2);
        builder.Property(x => x.SettlementDiscount).HasPrecision(18, 2);
        builder.Property(x => x.FinalPaymentAmount).HasPrecision(18, 2);
        builder.Property(x => x.BalanceBeforeSettlement).HasPrecision(18, 2);
        builder.HasOne<ThakaProject>()
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Restrict); builder.HasOne<ThakaPayment>()
            .WithMany()
            .HasForeignKey(x => x.FinalPaymentId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.SettlementNumber).IsUnique();
        builder.HasIndex(x => x.ClientOperationId).IsUnique();
        builder.HasIndex(x => new { x.ProjectId, x.SettledAt });
    }
}

internal sealed class ThakaReopeningConfiguration
    : IEntityTypeConfiguration<ThakaReopening>
{
    public void Configure(EntityTypeBuilder<ThakaReopening> builder)
    {
        builder.ToTable("reopenings", "thaka");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        builder.HasOne<ThakaProject>()
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ThakaSettlement>()
            .WithMany()
            .HasForeignKey(x => x.SettlementId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.ProjectId, x.ReopenedAt });
    }
}

internal sealed class ThakaMaterialReversalConfiguration
    : IEntityTypeConfiguration<ThakaMaterialReversal>
{
    public void Configure(EntityTypeBuilder<ThakaMaterialReversal> builder)
    {
        builder.ToTable(
            "material_reversals",
            "thaka",
            table => table.HasCheckConstraint(
                "ck_thaka_material_reversal_values",
                "reversed_charge >= 0 AND restored_cost >= 0"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ReversalNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.ReversedCharge).HasPrecision(18, 2);
        builder.Property(x => x.RestoredCost).HasPrecision(18, 6);
        builder.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        builder.HasOne<ThakaProject>()
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ThakaMaterialIssue>()
            .WithMany()
            .HasForeignKey(x => x.MaterialIssueId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.ReversalNumber).IsUnique();
        builder.HasIndex(x => x.ClientOperationId).IsUnique();
        builder.HasIndex(x => x.MaterialIssueId).IsUnique();
    }
}

internal sealed class ThakaPaymentReversalConfiguration
    : IEntityTypeConfiguration<ThakaPaymentReversal>
{
    public void Configure(EntityTypeBuilder<ThakaPaymentReversal> builder)
    {
        builder.ToTable(
            "payment_reversals",
            "thaka",
            table => table.HasCheckConstraint(
                "ck_thaka_payment_reversal_amount_positive",
                "amount > 0")); builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ReversalNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Amount).HasPrecision(18, 2);
        builder.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        builder.HasOne<ThakaProject>()
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ThakaPayment>()
            .WithMany()
            .HasForeignKey(x => x.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.ReversalNumber).IsUnique();
        builder.HasIndex(x => x.ClientOperationId).IsUnique();
        builder.HasIndex(x => x.PaymentId).IsUnique();
    }
}
