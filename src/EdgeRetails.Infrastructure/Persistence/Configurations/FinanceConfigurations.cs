using EdgeRetails.Domain.Finance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EdgeRetails.Infrastructure.Persistence.Configurations;

internal sealed class CashSessionConfiguration : IEntityTypeConfiguration<CashSession>
{
    public void Configure(EntityTypeBuilder<CashSession> builder)
    {
        builder.ToTable(
            "cash_sessions",
            "finance",
            table => table.HasCheckConstraint(
                "ck_cash_session_amounts",
                "opening_cash >= 0 AND (counted_closing_cash IS NULL OR counted_closing_cash >= 0)"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.OpeningCash).HasPrecision(18, 2);
        builder.Property(x => x.ExpectedClosingCash).HasPrecision(18, 2);
        builder.Property(x => x.CountedClosingCash).HasPrecision(18, 2);
        builder.Property(x => x.Difference).HasPrecision(18, 2);
        builder.Property(x => x.Note).HasMaxLength(1000);
        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasIndex(x => x.Status)
            .IsUnique()
            .HasFilter("status = 1");
        builder.HasIndex(x => x.BusinessDate);
    }
}

internal sealed class CashMovementConfiguration : IEntityTypeConfiguration<CashMovement>
{
    public void Configure(EntityTypeBuilder<CashMovement> builder)
    {
        builder.ToTable(
            "cash_movements",
            "finance",
            table => table.HasCheckConstraint(
                "ck_cash_movement_amount_positive",
                "amount > 0"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Amount).HasPrecision(18, 2);
        builder.Property(x => x.SourceType).HasMaxLength(80);
        builder.Property(x => x.Reason).HasMaxLength(250);
        builder.Property(x => x.Note).HasMaxLength(1000);
        builder.Ignore(x => x.SignedAmount);

        builder.HasOne<CashSession>()
            .WithMany()
            .HasForeignKey(x => x.CashSessionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.CashSessionId, x.OccurredAt });
        builder.HasIndex(x => new { x.SourceType, x.SourceId });
    }
}
