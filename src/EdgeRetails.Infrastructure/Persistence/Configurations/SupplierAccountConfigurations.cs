using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Parties;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class SupplierAccountEntryConfiguration : IEntityTypeConfiguration<SupplierAccountEntry>
{
    public void Configure(EntityTypeBuilder<SupplierAccountEntry> builder)
    {
        builder.ToTable(
            "supplier_account_entries",
            "finance",
            table => table.HasCheckConstraint("ck_supplier_account_entry_amount_positive", "amount > 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.EntryNumber).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Amount).HasPrecision(18, 2);
        builder.Property(x => x.ReferenceType).HasMaxLength(80).IsRequired();
        builder.Property(x => x.Note).HasMaxLength(1000);
        builder.Ignore(x => x.SignedAmount);

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(x => x.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.EntryNumber).IsUnique();
        builder.HasIndex(x => new { x.SupplierId, x.OccurredAt, x.Id });
        builder.HasIndex(x => x.ClientOperationId);
        builder.HasIndex(x => new { x.EntryType, x.ReferenceType, x.ReferenceId })
            .IsUnique()
            .HasFilter("reference_id IS NOT NULL");
    }
}

internal sealed class SupplierPaymentConfiguration : IEntityTypeConfiguration<SupplierPayment>
{
    public void Configure(EntityTypeBuilder<SupplierPayment> builder)
    {
        builder.ToTable(
            "supplier_payments",
            "finance",
            table => table.HasCheckConstraint("ck_supplier_payment_amount_positive", "amount > 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.PaymentNumber).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Amount).HasPrecision(18, 2);
        builder.Property(x => x.ExternalReference).HasMaxLength(250);
        builder.Property(x => x.Note).HasMaxLength(1000);

        builder.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CashSession>().WithMany().HasForeignKey(x => x.CashSessionId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.PaymentNumber).IsUnique();
        builder.HasIndex(x => x.ClientOperationId).IsUnique();
        builder.HasIndex(x => new { x.SupplierId, x.PaidAt, x.Id });
    }
}

internal sealed class SupplierPaymentReversalConfiguration : IEntityTypeConfiguration<SupplierPaymentReversal>
{
    public void Configure(EntityTypeBuilder<SupplierPaymentReversal> builder)
    {
        builder.ToTable("supplier_payment_reversals", "finance");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        builder.HasOne<SupplierPayment>().WithMany().HasForeignKey(x => x.SupplierPaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.SupplierPaymentId).IsUnique();
        builder.HasIndex(x => x.ClientOperationId).IsUnique();
    }
}

internal sealed class SupplierRefundConfiguration : IEntityTypeConfiguration<SupplierRefund>
{
    public void Configure(EntityTypeBuilder<SupplierRefund> builder)
    {
        builder.ToTable(
            "supplier_refunds",
            "finance",
            table => table.HasCheckConstraint("ck_supplier_refund_amount_positive", "amount > 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.RefundNumber).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Amount).HasPrecision(18, 2);
        builder.Property(x => x.ExternalReference).HasMaxLength(250);
        builder.Property(x => x.ReferenceType).HasMaxLength(80);
        builder.Property(x => x.Note).HasMaxLength(1000);

        builder.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CashSession>().WithMany().HasForeignKey(x => x.CashSessionId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.RefundNumber).IsUnique();
        builder.HasIndex(x => x.ClientOperationId).IsUnique();
        builder.HasIndex(x => new { x.SupplierId, x.ReceivedAt, x.Id });
    }
}

internal sealed class SupplierRefundReversalConfiguration : IEntityTypeConfiguration<SupplierRefundReversal>
{
    public void Configure(EntityTypeBuilder<SupplierRefundReversal> builder)
    {
        builder.ToTable("supplier_refund_reversals", "finance");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        builder.HasOne<SupplierRefund>().WithMany().HasForeignKey(x => x.SupplierRefundId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.SupplierRefundId).IsUnique();
        builder.HasIndex(x => x.ClientOperationId).IsUnique();
    }
}
